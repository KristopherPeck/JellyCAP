# JellyCAP

JellyCAP (Jellyfin Collection Annotation Plugin) is an administrator-only Jellyfin plugin that drafts collection overviews, genres, tags, sort titles, content ratings, and community ratings from the metadata of the items already in each collection. Every generated result is stored as a proposal and must be reviewed before it can change Jellyfin metadata.

The initial release targets Jellyfin 12.3 and .NET 10.

## What it does

- Lists collections in an embedded Jellyfin dashboard.
- Generates overviews, genres, tags, sort titles, content ratings, and community ratings for selected collections.
- Community ratings can use a one-decimal average of every eligible rated member or an optional AI suggestion.
- Supports the OpenAI Responses API, Anthropic Messages API, and OpenAI-compatible Chat Completions endpoints.
- Lets an administrator independently enable overviews, genres, tags, sort titles, content ratings, and community ratings.
- Supports replace, merge, and only-if-empty policies as appropriate for each field.
- Presents current and proposed values side by side and allows edits before approval.
- Shows the current pending-review count on the JellyCAP dashboard.
- Paginates collections 50 at a time and pending reviews 25 at a time.
- Durably queues generation requests from individual collections or every collection matching the current search and activity filter, then runs up to a configurable number concurrently even when the dashboard is closed.
- Cancels individual or all queued jobs before they start, retries failed collections, and skips collections with pending reviews during Queue All by default.
- Tracks each collection's successful generation count, last attempt, last success, last application, current queue state, pending-review state, and most recent failure independently of proposal retention.
- Stays on the collection list after generation by default, with an option to open Pending reviews automatically.
- Prunes approved and rejected proposal history using configurable age and count limits while preserving every pending proposal.
- Writes handled provider, storage, configuration, and review failures through Jellyfin's logging system.
- Enforces English-script output when English is selected, while preserving non-Latin official titles and names copied from the supplied metadata.
- Detects metadata changed after generation and asks before overwriting it.
- Retains rejected and applied proposal history according to the configured policy without sending provider credentials to the browser.
- Stores proposals, durable jobs, and compact per-collection generation history in a plugin-owned SQLite database with indexed status and timestamp fields.

## TV collection behavior

TV series are treated as terminal collection members. JellyCAP includes the series title and series-level metadata, but it does not recursively collect seasons or episodes. If Jellyfin returns a season or episode as a linked collection member, it is explicitly discarded.

Nested collections are flattened with a cycle guard. Movies and other non-folder media items are included, while unrelated folder containers are skipped.

## Provider data boundary

The provider receives a bounded JSON context containing only:

- Collection name and current sort title, content rating, community rating, overview, genres, and tags
- The calculated child community-rating average and the number of rated eligible members
- Eligible child type, name, sort title, content rating, community rating, production year, overview, genres, and tags
- The number of eligible items omitted by the configured limit

File paths, media streams, provider identifiers, user history, and credentials are not included. Child metadata is marked as untrusted data at both the system-instruction and prompt levels to reduce prompt-injection risk. Context is bounded by both the configured item count and a hard 200,000-character ceiling to limit memory use and accidental provider cost.

API keys can be stored in the plugin data directory or loaded from an environment variable. The environment variable takes precedence. Dedicated environment variables must use the `JELLYCAP_` prefix; `OPENAI_API_KEY` and `ANTHROPIC_API_KEY` are accepted only for their matching provider with its default endpoint. This prevents the plugin from being configured to read unrelated process secrets. The settings API reports only whether a key exists and never returns the key itself.

Stored keys are plaintext server-side secrets, like many Jellyfin plugin credentials. On Unix systems JellyCAP restricts the secret file to its owner; Windows installations rely on the Jellyfin data directory's ACLs. Protect and back up that directory accordingly, and prefer a dedicated environment variable when the host's secret-management setup supports one.

All plugin API routes require Jellyfin's elevated-privilege authorization policy. Provider redirects are disabled, response bodies are capped at 2 MB, provider error text is bounded and API keys are redacted, SQL statements are parameterized, and browser-rendered metadata uses text nodes rather than HTML injection. Non-loopback plaintext HTTP endpoints require an explicit opt-in; use only endpoints trusted to receive the selected collection metadata and provider credential. A server-wide limit of five simultaneous provider requests also covers queue workers and provider connection tests.

## Review workflow

1. Configure the provider and generation preferences in **Dashboard → JellyCAP → Settings**.
2. Test the provider connection.
3. Open **Collections** and generate a proposal for one collection.
4. Review and optionally edit each proposed field under **Pending reviews**.
5. Select the fields to apply, then approve or reject the proposal.

The pending-review badge updates after generation, approval, rejection, and review refresh. Enable **Open Pending reviews after generating a proposal** in Settings if you prefer the dashboard to switch to the new proposal automatically.

The Jellyfin server runs two collection generations concurrently by default, configurable from 1 through 5. Additional selections are stored in SQLite and display their state on the collection card. Queue all collections confirms the complete filtered batch size and respects both the current text search and activity filter. Closing or reloading the dashboard does not stop queued work. A Jellyfin restart returns interrupted running jobs to the queue. Automatic navigation to Pending reviews occurs only when the dashboard session that submitted work remains open and the server queue finishes.

Queued jobs use the current saved generation settings and credential when a worker starts them; credentials are never copied into the job table. Changing settings therefore affects jobs that have not started yet.

Queued collection cards can be cancelled individually, and **Cancel all queued** removes every job that has not started. Running provider requests continue. Failed cards expose **Retry generation**, which creates a new job without resetting the collection's successful-generation history. Queue All skips collections with pending reviews by default; clear that option when intentionally generating another proposal for them.

Collection activity filters include all, never generated, generated previously, queued, generating, last attempt failed, applied previously, and pending review. Successful generation totals and timestamps are retained after approved or rejected proposal records expire. When upgrading the database, retained proposals seed the initial totals; generations already removed by an older retention policy cannot be reconstructed.

Completed proposal retention applies to approved and rejected proposals as one combined history. The defaults retain completed proposals for 7 days, capped at the newest 100 records. Setting either completed-history limit to 0 removes completed proposals immediately. Pending proposals are never removed by retention.

Jellyfin's supported plugin-page registration does not expose a dynamic badge or admin-home widget hook, so the live count is displayed inside the JellyCAP dashboard. Adding it to Jellyfin's own navigation or admin home would require an unsupported web-client modification.

When a proposal is generated, JellyCAP fingerprints the collection's current sort title, content rating, community rating, overview, genres, and tags. If those fields change before approval, the normal apply request stops with a conflict instead of silently overwriting the newer metadata. The dashboard then offers an explicit force option.

Content rating suggestions are constrained to exact rating values already present on eligible child items. A provider-supplied value outside that set is discarded and surfaced as a review warning. When no child rating is available, the current collection rating is preserved.

## Development and packaging

The project is a conventional Jellyfin server plugin and does not require Node.js or Docker. Restore and publish it on a trusted .NET 10 development workstation:

```powershell
dotnet restore
dotnet publish -c Release
```

Copy the published JellyCAP plugin files into a `JellyCAP` directory beneath Jellyfin's plugin directory, then restart Jellyfin. The project enables `CopyLocalLockFileAssemblies` so the output includes the managed `Microsoft.Data.Sqlite`, `SQLitePCLRaw.core`, and `SQLitePCLRaw.provider.e_sqlite3` assemblies required by Jellyfin's isolated plugin loader. It must not include a `runtimes` directory or native `e_sqlite3` library. JellyCAP initializes its managed provider against the native SQLite runtime already installed with Jellyfin. The project includes a publish-time guard that fails if a native SQLite file enters the publish output.

When upgrading from a build that bundled SQLite, publish to a clean output directory. Then stop Jellyfin and remove the old plugin `runtimes` directory, any plugin-local native `e_sqlite3` files, and `SQLitePCLRaw.batteries_v2.dll` before copying the replacement files. Keep the newly published managed SQLite assemblies. Remove files only from the JellyCAP/JellyAnnotator plugin directory, not from Jellyfin's server installation. Jellyfin scans plugin DLLs recursively and can otherwise attempt to load a native SQLite library as a managed plugin assembly.

## Proposal database and migration

Proposals are stored in `jellycap.db` inside the plugin data directory. The database uses write-ahead logging, parameterized queries, transactional updates, and indexes for status, creation time, and completion time. Proposal payloads remain self-contained JSON documents inside individual database rows so additive proposal fields can remain backward-compatible without returning to a single-file store.

On the first database access after upgrading, JellyCAP transactionally imports an existing `proposals.json`. Only after the import and migration marker commit successfully does it rename the old file to `proposals.json.migrated.bak`. A malformed or unreadable legacy file stops the migration and is logged rather than being silently discarded. The migration is idempotent and will not re-import the backup on later starts.

## Current scope

- Generation is manually initiated from the dashboard, either collection-by-collection or through the durable server-side batch queue; there is no scheduled generation.
- Proposals are persisted in a dedicated SQLite database; collection and pending-review API responses are paginated.
- Only administrators can access the plugin API and dashboard.
- Provider calls are made directly by the Jellyfin server.
- The initial version does not schedule bulk jobs, generate images, or annotate ordinary library items.

## License

[MIT](LICENSE)
