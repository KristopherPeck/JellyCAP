# Changelog

All notable changes to JellyCAP are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed

- Collection pages and dashboard polling now query durable activity only for the visible collection IDs instead of loading every recorded collection status.

## [0.7.0.0] - 2026-11-03

### Added

- Added durable apply-intent recovery so interrupted metadata approvals can be reconciled safely after Jellyfin restarts.

### Changed

- Stabilized global, server queue, and settings status messages so updates no longer shift surrounding dashboard content; truncated text remains available as a tooltip.
- Bulk approval and rejection now process pending proposals in bounded batches instead of loading the full queue into memory.
- Unexpected dashboard errors now provide a Jellyfin log reference without exposing internal exception details.

### Fixed

- Made settings and stored-credential updates rollback-aware, prevented replacement keys from being deleted before they are written, and separated retention maintenance from the settings commit.
- Persisted metadata-application intent before updating Jellyfin and finalized the proposal state with non-cancellable storage after a successful metadata write.

## [0.6.0.0] - 2026-11-02

### Changed

- Reduced the space between the dashboard navigation and each section's controls.
- Collection activity summaries now reserve active queue and generation messaging for the dedicated status controls instead of adding duplicate lines.
- Pending reviews now hide metadata fields disabled in the saved settings, prevent empty individual approvals, and exclude disabled fields from bulk approval.
- Manual installation instructions now use exact four-part versioned directories and include safer upgrade guidance to prevent conflicts with catalog-installed releases.

## [0.5.0.0] - 2026-10-31

### Added

- Optional local calculation of collection year and release date from the earliest eligible movies or TV series, with reviewable field-level application controls.

### Changed

- Collection cards now hide metadata fields that are disabled in the saved generation settings while pending reviews continue to show every proposed field.

## [0.4.0.0] - 2026-10-30

### Changed

- Restyled dashboard section navigation to match Jellyfin's understated tab-bar pattern.

### Fixed

- Prevented the release script from wrapping single-plugin manifests in PowerShell array metadata.

## [0.3.0.0] - 2026-10-29

### Added

- Configurable server-wide spacing between AI provider requests to help avoid provider rate limits.
- Collection filtering for metadata that has never been applied through JellyCAP.
- Bulk approval for all pending proposals, with stale and failed proposals left pending for review.

### Changed

- Reduced collection dashboard pages to 30 entries so the three-column layout ends on a complete row.

### Fixed

- Preserved the pending-review scroll position after approving or rejecting a proposal.
- Aligned generation checkboxes into consistent responsive columns.

## [0.2.0.0] - 2026-10-28

### Added

- Bulk rejection for all pending metadata proposals from the review screen.
- Browser-local persistence for the selected collection and pending-review sort orders.
- Added Catalog artwork

### Fixed

- Normalized OpenAI provider names, improved provider-test error details, refined generated-tag guidance, and polished dashboard wording.

## [0.1.0.0] - 2026-10-27

Beta 1 is the first public test release of JellyCAP for Jellyfin 12.4.

### Added

- Administrator dashboard for generating, reviewing, editing, approving, and rejecting collection metadata proposals.
- OpenAI Responses, Anthropic Messages, and OpenAI-compatible Chat Completions provider support.
- Suggestions for overviews, genres, tags, sort titles, content ratings, and optional AI-generated community ratings.
- Locally calculated community ratings based on eligible collection members.
- TV-aware collection context that uses series metadata without including seasons or episodes.
- Durable SQLite-backed generation queue that continues after the dashboard closes and recovers interrupted jobs after a server restart.
- Individual generation, Queue All, cancellation, failed-job retry, and pending-review skip controls.
- Per-collection generation totals, activity timestamps, queue state, failure state, and activity filters.
- Paginated collection and pending-review views, configurable proposal retention, and stale-metadata conflict detection.
- English-script output enforcement, bounded provider context and responses, endpoint validation, redirect blocking, secret redaction, and administrator-only routes.
- Jellyfin-integrated error logging for provider, configuration, storage, queue, and review failures.

### Changed

- Aligned the project license with Jellyfin's preferred GPL-3.0-only license for public plugins.
- Added packaged third-party license and attribution notices.
- Updated Microsoft.Data.Sqlite.Core to 10.0.14.
- Updated the Jellyfin plugin ABI and server package references to 12.4.

### Notes

- This is beta software. Back up Jellyfin metadata and test with a small number of collections before using Queue All.
- Generated metadata is always presented for administrator review before it can be applied.

[Unreleased]: https://github.com/KristopherPeck/JellyCAP/compare/0.7.0.0...HEAD
[0.7.0.0]: https://github.com/KristopherPeck/JellyCAP/releases/tag/0.7.0.0
[0.6.0.0]: https://github.com/KristopherPeck/JellyCAP/releases/tag/0.6.0.0
[0.5.0.0]: https://github.com/KristopherPeck/JellyCAP/releases/tag/0.5.0.0
[0.4.0.0]: https://github.com/KristopherPeck/JellyCAP/releases/tag/0.4.0.0
[0.3.0.0]: https://github.com/KristopherPeck/JellyCAP/releases/tag/0.3.0.0
[0.2.0.0]: https://github.com/KristopherPeck/JellyCAP/releases/tag/0.2.0.0
[0.1.0.0]: https://github.com/KristopherPeck/JellyCAP/releases/tag/0.1.0.0
