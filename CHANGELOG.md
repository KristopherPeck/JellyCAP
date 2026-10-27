# Changelog

All notable changes to JellyCAP are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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

[Unreleased]: https://github.com/KristopherPeck/JellyCAP/compare/0.1.0.0...HEAD
[0.1.0.0]: https://github.com/KristopherPeck/JellyCAP/releases/tag/0.1.0.0
