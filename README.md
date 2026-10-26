# JellyAnnotator

JellyAnnotator is an administrator-only Jellyfin plugin that drafts collection overviews, genres, tags, sort titles, and content ratings from the metadata of the items already in each collection. Every generated result is stored as a proposal and must be reviewed before it can change Jellyfin metadata.

The initial release targets Jellyfin 12.3 and .NET 10.

## What it does

- Lists collections in an embedded Jellyfin dashboard.
- Generates metadata for one selected collection at a time.
- Supports the OpenAI Responses API, Anthropic Messages API, and OpenAI-compatible Chat Completions endpoints.
- Lets an administrator independently enable overviews, genres, tags, sort titles, and content ratings.
- Supports replace, merge, and only-if-empty policies as appropriate for each field.
- Presents current and proposed values side by side and allows edits before approval.
- Shows the current pending-review count on the JellyAnnotator dashboard.
- Paginates collections 50 at a time and pending reviews 25 at a time.
- Queues generation requests from multiple collections, disables each queued/running collection button, and runs up to a configurable number concurrently.
- Stays on the collection list after generation by default, with an option to open Pending reviews automatically.
- Prunes approved and rejected proposal history using configurable age and count limits while preserving every pending proposal.
- Writes handled provider, storage, configuration, and review failures through Jellyfin's logging system.
- Detects metadata changed after generation and asks before overwriting it.
- Retains rejected and applied proposal history according to the configured policy without sending provider credentials to the browser.

## TV collection behavior

TV series are treated as terminal collection members. JellyAnnotator includes the series title and series-level metadata, but it does not recursively collect seasons or episodes. If Jellyfin returns a season or episode as a linked collection member, it is explicitly discarded.

Nested collections are flattened with a cycle guard. Movies and other non-folder media items are included, while unrelated folder containers are skipped.

## Provider data boundary

The provider receives a bounded JSON context containing only:

- Collection name and current sort title, content rating, overview, genres, and tags
- Eligible child type, name, sort title, content rating, production year, overview, genres, and tags
- The number of eligible items omitted by the configured limit

File paths, media streams, provider identifiers, user history, and credentials are not included. Child metadata is marked as untrusted data in the prompt to reduce prompt-injection risk.

API keys can be stored in the plugin data directory or loaded from an environment variable. The environment variable takes precedence. The settings API reports only whether a key exists and never returns the key itself.

Stored keys are plaintext server-side secrets, like many Jellyfin plugin credentials. Protect and back up the Jellyfin data directory accordingly; prefer an environment variable when the host's secret-management setup supports one.

## Review workflow

1. Configure the provider and generation preferences in **Dashboard → JellyAnnotator → Settings**.
2. Test the provider connection.
3. Open **Collections** and generate a proposal for one collection.
4. Review and optionally edit each proposed field under **Pending reviews**.
5. Select the fields to apply, then approve or reject the proposal.

The pending-review badge updates after generation, approval, rejection, and review refresh. Enable **Open Pending reviews after generating a proposal** in Settings if you prefer the dashboard to switch to the new proposal automatically.

The dashboard runs two collection generations concurrently by default, configurable from 1 through 5. Additional selections remain queued and display their state on the collection card. Automatic navigation to Pending reviews waits until the current browser-session queue finishes.

Completed proposal retention applies to approved and rejected proposals as one combined history. The defaults retain completed proposals for 7 days, capped at the newest 100 records. Setting either completed-history limit to 0 removes completed proposals immediately. Pending proposals are never removed by retention.

Jellyfin's supported plugin-page registration does not expose a dynamic badge or admin-home widget hook, so the live count is displayed inside the JellyAnnotator dashboard. Adding it to Jellyfin's own navigation or admin home would require an unsupported web-client modification.

When a proposal is generated, JellyAnnotator fingerprints the collection's current sort title, content rating, overview, genres, and tags. If those fields change before approval, the normal apply request stops with a conflict instead of silently overwriting the newer metadata. The dashboard then offers an explicit force option.

Content rating suggestions are constrained to exact rating values already present on eligible child items. A provider-supplied value outside that set is discarded and surfaced as a review warning. When no child rating is available, the current collection rating is preserved.

## Development and packaging

The project is a conventional Jellyfin server plugin and does not require Node.js or Docker. Restore and publish it on a trusted .NET 10 development workstation:

```powershell
dotnet restore
dotnet publish -c Release
```

Copy the published plugin assembly and its required plugin-local files into a `JellyAnnotator` directory beneath Jellyfin's plugin directory, then restart Jellyfin. Exact packaging and installation automation will be added when the project is prepared for repository distribution.

## Current scope

- Generation is intentionally manual and collection-by-collection.
- Proposals are persisted as JSON in the plugin data directory; collection and pending-review API responses are paginated.
- Only administrators can access the plugin API and dashboard.
- Provider calls are made directly by the Jellyfin server.
- The initial version does not schedule bulk jobs, generate images, or annotate ordinary library items.

## License

[MIT](LICENSE)
