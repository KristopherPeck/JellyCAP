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
- Queues generation requests from multiple collections, disables each queued/running collection button, and runs up to a configurable number concurrently.
- Stays on the collection list after generation by default, with an option to open Pending reviews automatically.
- Prunes approved and rejected proposal history using configurable age and count limits while preserving every pending proposal.
- Writes handled provider, storage, configuration, and review failures through Jellyfin's logging system.
- Enforces English-script output when English is selected, while preserving non-Latin official titles and names copied from the supplied metadata.
- Detects metadata changed after generation and asks before overwriting it.
- Retains rejected and applied proposal history according to the configured policy without sending provider credentials to the browser.
- Stores proposals in a plugin-owned SQLite database with indexed status and timestamp fields.

## TV collection behavior

TV series are treated as terminal collection members. JellyCAP includes the series title and series-level metadata, but it does not recursively collect seasons or episodes. If Jellyfin returns a season or episode as a linked collection member, it is explicitly discarded.

Nested collections are flattened with a cycle guard. Movies and other non-folder media items are included, while unrelated folder containers are skipped.

## Provider data boundary

The provider receives a bounded JSON context containing only:

- Collection name and current sort title, content rating, community rating, overview, genres, and tags
- The calculated child community-rating average and the number of rated eligible members
- Eligible child type, name, sort title, content rating, community rating, production year, overview, genres, and tags
- The number of eligible items omitted by the configured limit

File paths, media streams, provider identifiers, user history, and credentials are not included. Child metadata is marked as untrusted data in the prompt to reduce prompt-injection risk.

API keys can be stored in the plugin data directory or loaded from an environment variable. The environment variable takes precedence. The settings API reports only whether a key exists and never returns the key itself.

Stored keys are plaintext server-side secrets, like many Jellyfin plugin credentials. Protect and back up the Jellyfin data directory accordingly; prefer an environment variable when the host's secret-management setup supports one.

## Review workflow

1. Configure the provider and generation preferences in **Dashboard → JellyCAP → Settings**.
2. Test the provider connection.
3. Open **Collections** and generate a proposal for one collection.
4. Review and optionally edit each proposed field under **Pending reviews**.
5. Select the fields to apply, then approve or reject the proposal.

The pending-review badge updates after generation, approval, rejection, and review refresh. Enable **Open Pending reviews after generating a proposal** in Settings if you prefer the dashboard to switch to the new proposal automatically.

The dashboard runs two collection generations concurrently by default, configurable from 1 through 5. Additional selections remain queued and display their state on the collection card. Automatic navigation to Pending reviews waits until the current browser-session queue finishes.

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

- Generation is intentionally manual and collection-by-collection.
- Proposals are persisted in a dedicated SQLite database; collection and pending-review API responses are paginated.
- Only administrators can access the plugin API and dashboard.
- Provider calls are made directly by the Jellyfin server.
- The initial version does not schedule bulk jobs, generate images, or annotate ordinary library items.

## License

[MIT](LICENSE)
