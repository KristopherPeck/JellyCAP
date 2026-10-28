# JellyCAP

> AI-assisted collection metadata for Jellyfin, with an approval step before anything is changed.

[![Jellyfin 12.4](https://img.shields.io/badge/Jellyfin-12.4-00A4DC?logo=jellyfin&logoColor=white)](https://jellyfin.org/)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Beta](https://img.shields.io/badge/status-beta-orange)](https://github.com/KristopherPeck/JellyCAP/releases)
[![GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-blue)](LICENSE.md)

JellyCAP (Jellyfin Collection Annotation Plugin) creates reviewable metadata proposals from the items already inside a Jellyfin collection. It can suggest overviews, genres, tags, sort titles, content ratings, and community ratings without immediately overwriting the collection.

Every proposal can be inspected and edited by a Jellyfin administrator before selected fields are applied.

> [!WARNING]
> JellyCAP is currently beta software. Back up your Jellyfin data and begin with a small, non-critical collection before using **Queue all**.


## AI development disclosure

JellyCAP was designed and developed collaboratively by the project maintainer with assistance from OpenAI Codex. AI was used during research, implementation, refactoring, security review, troubleshooting, and documentation. AI-generated suggestions are treated as drafts rather than authoritative results; the maintainer directs the project, reviews changes, and is responsible for validating release candidates before publication.

This development disclosure is separate from JellyCAP's runtime AI features. Collection metadata is sent only to the provider an administrator explicitly configures, as described in [Privacy and security](#privacy-and-security).


## Screenshots

### Collection dashboard

Search and filter collections, see prior generation activity, queue individual collections, or queue every collection matching the current filters.

![JellyCAP collection dashboard](screenshots/Screenshot%202026-10-26%20184555.png)

### Proposal review

Compare existing and proposed metadata, edit the result, choose individual fields, and then approve or reject the proposal.

![JellyCAP proposal review](screenshots/Screenshot%202026-10-26%20184535.png)

## Features

- Generates reviewable collection overviews, genres, tags, sort titles, and content ratings
- Calculates a community-rating average from rated collection members
- Optionally asks the configured AI provider to suggest a community rating
- Supports OpenAI, Anthropic, and OpenAI-compatible endpoints
- Lets administrators edit proposals and apply only selected fields
- Supports replace, merge, and only-if-empty update policies where appropriate
- Keeps TV context at the series level; seasons and episodes are excluded
- Flattens nested collections while protecting against collection cycles
- Queues one collection or all collections matching the current search and activity filters
- Continues queued work after the dashboard closes and recovers interrupted jobs after a Jellyfin restart
- Tracks generation totals, attempts, successes, no-change results, applications, failures, and pending reviews per collection
- Automatically closes semantically unchanged results instead of adding them to Pending reviews
- Sorts collection and pending-review pages by collection name
- Detects collection metadata changed after generation before allowing an overwrite
- Stores proposals and queue state in a plugin-owned SQLite database
- Logs provider, storage, configuration, queue, and review errors through Jellyfin

## Requirements

- Jellyfin Server **12.4**
- A host running Jellyfin on a currently supported, security-patched **.NET 10** runtime (10.0.14 or newer for this beta)
- Administrator access to configure and use the plugin
- An API key for OpenAI or Anthropic, or access to a compatible endpoint

## Installation

### Plugin repository

1. Open **Dashboard → Plugins → Repositories**.
2. Select **Add**.
3. Name the repository `JellyCAP` and use this URL:

   ```text
   https://raw.githubusercontent.com/KristopherPeck/JellyCAP/main/manifest.json
   ```

4. Open **Catalog** and install JellyCAP from the **Metadata** category.
5. Restart Jellyfin.
6. Open **Dashboard → JellyCAP → Settings**.

### Manual installation

1. Download `JellyCAP_<version>.zip` from the matching [GitHub release](https://github.com/KristopherPeck/JellyCAP/releases).
2. Extract the archive into a `JellyCAP` directory beneath Jellyfin's plugin directory.
3. Restart Jellyfin.

Common plugin locations include:

| Installation | Plugin directory |
| --- | --- |
| Windows tray install | `%ProgramData%\Jellyfin\Server\plugins` |
| Windows user install | `%LocalAppData%\jellyfin\plugins` |
| Common Linux packages | `/var/lib/jellyfin/plugins` |

Do not install a plugin-local `runtimes` directory or native `e_sqlite3` file. JellyCAP uses the native SQLite runtime supplied by Jellyfin.

## Getting started

1. Open **Dashboard → JellyCAP → Settings**.
2. Choose a provider protocol, endpoint, model, and credential source.
3. Select which metadata fields JellyCAP should propose.
4. Configure each field's update behavior and save the settings.
5. Use **Test connection** to verify the provider.
6. Open **Collections** and generate a proposal for one collection.
7. Open **Pending reviews**, inspect the result, and apply only the fields you want.

The server runs two generation jobs concurrently by default. This can be configured from one through five. Additional requests wait in SQLite, so closing or refreshing the browser does not cancel them.

## Metadata behavior

| Field | How JellyCAP proposes it |
| --- | --- |
| Overview | AI-generated in the selected language and overview style |
| Genres | AI-generated from eligible collection-member metadata |
| Tags | AI-generated from eligible collection-member metadata |
| Sort title | AI-generated to provide consistent collection sorting |
| Content rating | AI chooses only from exact ratings already found on eligible members |
| Community rating | Local one-decimal member average, or an optional AI suggestion |

When English is selected, JellyCAP checks generated prose for unexpected non-Latin text while allowing official names and titles copied from the source metadata. If no eligible child content rating exists, the collection's current rating is preserved.

### TV and nested collections

TV series are terminal members for context generation. JellyCAP includes series-level metadata but explicitly discards seasons and episodes, even if Jellyfin returns them as directly linked collection members.

Nested collections are flattened with a cycle guard. Movies and other eligible media items are included; unrelated folder containers are skipped.

### Review and conflict protection

Generating a proposal does not update Jellyfin metadata. JellyCAP fingerprints the collection's current editable fields when generation begins. If those fields change before approval, the normal apply operation stops and asks for explicit confirmation before overwriting newer metadata.

Queue All skips collections with pending reviews by default. Queued jobs can be cancelled before they start, failed or unchanged results can be rerun, and completed proposal history can be pruned by age and count. Pending proposals are never removed by retention.

## Supported providers

- OpenAI Responses API
- Anthropic Messages API
- OpenAI-compatible Chat Completions endpoints

Custom endpoints must use HTTPS unless they are loopback addresses or the administrator explicitly enables non-loopback HTTP. Provider redirects are disabled. A server-wide maximum of five simultaneous provider requests covers generation workers and connection tests.

API keys may be saved in the plugin data directory or supplied through an environment variable. Stored keys are written as plaintext JSON and rely on the operating system permissions protecting Jellyfin's plugin data directory; JellyCAP does not encrypt them at rest. If you do not want JellyCAP to write a credential to disk, use an environment variable instead. Environment variables take precedence. Dedicated variables must use the `JELLYCAP_` prefix; `OPENAI_API_KEY` and `ANTHROPIC_API_KEY` are accepted only with their matching provider and default endpoint.

### API key environment variables

Environment variables belong to the Jellyfin server process, not the browser or the administrator account opening the dashboard. Set the variable before Jellyfin starts, restart the Jellyfin server or service, and enter only its name in JellyCAP's **API key environment variable** setting. Leave the API key field blank; after saving, JellyCAP should report the key source as `environment`. You may then select **Remove stored API key when saving** if an older key was saved on disk.

On Windows:

1. Open **System Properties**, select **Advanced**, then **Environment Variables**.
2. If Jellyfin is launched interactively, from the tray, or from a normal startup shortcut, add a **User variable** for the Windows account that launches Jellyfin. If Jellyfin runs as a Windows service or under another account, add a **System variable** instead. Use a name such as `JELLYCAP_OPENAI_API_KEY` and place the provider key in its value.
3. Fully exit and relaunch Jellyfin. For a service installation, restart the Jellyfin service. If the new process still does not receive the variable, restart Windows.
4. Enter `JELLYCAP_OPENAI_API_KEY` in JellyCAP's environment-variable setting and save.

Avoid putting the key directly in a PowerShell command because it may be retained in shell history. For a Linux systemd installation, prefer a root-controlled `EnvironmentFile` referenced by a Jellyfin service override, restrict that file to the Jellyfin service account, and restart the service after changes.

Environment variables avoid JellyCAP's plaintext `secrets.json`, but they are not encrypted storage. Administrators and sufficiently privileged processes on the Jellyfin host may still inspect the server process environment.

## Privacy and security

JellyCAP sends a bounded metadata context to the configured provider. This can include collection and eligible-member names, overviews, genres, tags, ratings, sort titles, production years, and media types.

It does **not** send file paths, media streams, provider identifiers, Jellyfin user history, or provider credentials as prompt content. Child metadata is treated as untrusted input, request and response sizes are bounded, and credentials are not returned to the browser.

All JellyCAP API routes require Jellyfin's elevated-privilege authorization policy. See [SECURITY.md](SECURITY.md) for private vulnerability reporting and additional security guidance.

## Storage and upgrades

Proposals, queue jobs, and compact collection activity are stored in `jellycap.db` inside the plugin data directory. The database uses transactions, write-ahead logging, parameterized queries, and indexed status and timestamp fields.

On first access after upgrading from the original JSON-backed build, JellyCAP imports `proposals.json` transactionally. After a successful import it renames the source file to `proposals.json.migrated.bak`. An unreadable legacy file is logged and retained rather than discarded.

When upgrading from a build that bundled SQLite, stop Jellyfin and remove the old plugin-local `runtimes` directory, native `e_sqlite3` files, and `SQLitePCLRaw.batteries_v2.dll` before copying the replacement files. Remove these only from the JellyCAP plugin directory, never from the Jellyfin server installation.

## Troubleshooting

Start with the Jellyfin server log. JellyCAP records handled provider, configuration, queue, proposal, and database failures there.

If Jellyfin disables the plugin while loading `e_sqlite3.dll`, an old native runtime is still present in the plugin directory. Remove the plugin-local `runtimes` directory and reinstall the current release archive.

If Jellyfin reports that `Microsoft.Data.Sqlite` cannot be found, reinstall from the complete JellyCAP release ZIP. Copying only `Jellyfin.Plugin.JellyCAP.dll` is not sufficient because the plugin also needs its managed SQLite assemblies.

For reproducible problems, open a [bug report](https://github.com/KristopherPeck/JellyCAP/issues/new/choose) with JellyCAP and Jellyfin versions, reproduction steps, and sanitized logs. Never attach API keys, authorization headers, private endpoint credentials, or sensitive media metadata.

## Current limitations

- Generation is administrator-initiated; there is no time-based schedule
- JellyCAP annotates collections, not ordinary library items
- Image generation is not supported
- Jellyfin does not expose a supported dynamic badge or admin-home widget hook, so the pending count appears inside the JellyCAP dashboard
- Provider calls are made directly by the Jellyfin server

## Development

JellyCAP is a conventional .NET Jellyfin server plugin. It does not require Node.js or Docker. Use a security-patched .NET 10 SDK (10.0.14 or newer for this beta).

```powershell
dotnet restore
dotnet publish -c Release
```

The publish directory alone may omit managed SQLite dependencies and should not be used as a release archive. To create a complete candidate package on a trusted .NET 10 workstation, run:

```powershell
pwsh .\scripts\package-release.ps1 -Version 0.1.0.0
```

The script stages the plugin, XML documentation, and required managed SQLite assemblies; verifies the plugin DLL version; excludes debug symbols and native SQLite assets; and creates the release ZIP plus MD5 and SHA-256 checksum files under `artifacts`. It also prints a manifest-ready UTC timestamp.

To create the ZIP and update its existing version entry in `manifest.json` in one command, run:

```powershell
pwsh .\scripts\package-release.ps1 -Version 0.1.0.0 -UpdateManifest
```

Use `-UpdateManifest` only for the final candidate you intend to upload, then test the generated ZIP before publishing it. This option requires a fresh build and cannot be combined with `-SkipBuild`. The script refuses to update the manifest if the version entry is missing, duplicated, points to a differently named archive, or the built DLL version does not match the requested release.

The manual **Package beta candidate** workflow creates the same test artifact without publishing a release, creating a tag, or modifying the manifest.

## Contributing and support

- Review changes and release history in [CHANGELOG.md](CHANGELOG.md)
- Report bugs or request features through [GitHub Issues](https://github.com/KristopherPeck/JellyCAP/issues/new/choose)
- Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md)

Beta testing is especially useful for fresh installs, different provider protocols, movie and TV collections, queue recovery after restart, cancellation and retry behavior, stale-metadata conflicts, and retention cleanup.

## License

JellyCAP is licensed under the [GNU General Public License v3.0 only](LICENSE.md). Third-party component licenses and attribution notices are available in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
