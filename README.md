# MediaForge Jellyfin Connector

A Jellyfin plugin that allows **all signed-in Jellyfin users** to search the
movie and TV sources enabled in MediaForge.

- Request mode: users submit a request; Jellyfin administrators can review,
  approve, or reject it in the admin tab.
- Automatic mode: selected content is added to the MediaForge download queue
  immediately.
- Search all enabled MediaForge sources or select individual sources. Fast
  sources appear immediately while the remaining sources continue in parallel;
  each source uses the same 15-second deadline as MediaForge's UI.
- Browse clickable New, Popular, and Movies rows from MediaForge immediately
  when opening the Requests page.
- Automatically compare a title with Jellyfin's actual library and request
  only missing movies, seasons, or individual episodes. Complete titles are
  never queued again. Provider IDs are preferred; title and year provide a
  conservative fallback.
- Each user has a personal status view, while administrators have a shared
  overview. Requests are stored atomically in the plugin data file.
- Users can withdraw pending requests and view MediaForge download progress
  after approval. The plugin intentionally does not allow users to cancel a
  download once it has started.
- Server-side API connection: a stored MediaForge key is never returned to a
  browser and is not stored as plaintext in the Jellyfin plugin configuration.
- Adult sources remain blocked for API-key requests by MediaForge's central
  age gate and cannot be enabled from Jellyfin.

## Architecture

The project intentionally consists of two components:

1. `Jellyfin.Plugin.MediaForge`: the user interface, Jellyfin permissions,
   request database, and administrator approval flow.
2. `MediaForge.Module/mediaforge_jellyfin_connector`: a small MediaForge module
   that extends the existing external API with search, content resolution, and
   queue write access. Internally, it uses MediaForge's own handlers and
   providers.

MediaForge changes its internal Web UI endpoints more frequently than its
versioned API. The companion module keeps this dependency on the MediaForge
side and exposes an API-key-protected interface to the Jellyfin plugin.

### Optional Jellix integration

Version 0.4.0 adds protocol-v1 compatibility with the optional
`Jellix-for-Jellyfin` plugin. Jellix discovers the concrete in-process bridge
`Jellyfin.Plugin.MediaForge.Integration.JellixBridge` after both Jellyfin
plugins have been installed and Jellyfin has been restarted. No shared contract
DLL and no additional API key are required.

Jellix can search, submit requests, list the linked Jellyfin user's requests,
and monitor download state. Search selections use short-lived, user-bound,
single-use opaque tokens. Jellix never receives MediaForge URLs, episode lists,
the connector API key, or direct access to `requests.json`; both Jellyfin UIs
use the same request application service and atomic request store.

## Requirements

- Jellyfin 10.11 or later
- MediaForge 1.5.x or 1.6.x
- MediaForge must be reachable from the Jellyfin server over HTTP(S)
- .NET 9 SDK for local builds

MediaForge downloads the files. Its download directories must also be
accessible to Jellyfin as media libraries. When using Docker, mount the same
host directory in both containers, for example:

```yaml
volumes:
  - /srv/media:/media
```

MediaForge can then write to directories such as `/media/Movies` and
`/media/TV`, while Jellyfin reads the same paths as libraries.

## Installation

### 1. Install the MediaForge module

Copy the `MediaForge.Module/mediaforge_jellyfin_connector` directory to
`~/.mediaforge/thirdparties/mediaforge_jellyfin_connector` and restart
MediaForge. In **Module Manager > Module Settings**, confirm that
**Jellyfin Connector** is enabled.

The connector does not need a separate settings page in MediaForge. Its only
module-specific setting is the enable toggle. The API key is created centrally
under **Settings > API**. If the module card is missing, verify that
`~/.mediaforge/thirdparties/mediaforge_jellyfin_connector/__init__.py` exists
directly and that the module directory is not nested twice.

In MediaForge, open **Settings > API** and create a new scoped key with these
permissions:

```text
status:read
library:read
queue:read
queue:write
```

Copy the key immediately; MediaForge displays it only once.

### 2. Install the Jellyfin plugin from the repository

In Jellyfin, open **Dashboard > Plugins > Repositories > New Repository** and
enter:

```text
Name: MediaForge Requests
URL:  https://daseric.github.io/Mediaforge-Jellyfin-Connector/manifest.json
```

Install **MediaForge Requests** from the catalog and restart Jellyfin. Then
open **Dashboard > Plugins > My Plugins > MediaForge Requests > Settings**.
The page title must be **MediaForge Requests Settings** and includes the
password field **MediaForge API-Key**. Configure:

- MediaForge URL
- API key
- Request or automatic mode
- Allowed sources
- Default language and provider

Use **Test saved connection** to verify that the URL, key, scopes, and
MediaForge module work together.

The password field intentionally remains empty after saving. Entering a new
key replaces the existing one; a separate button is available to remove it.
Keys stored as plaintext by older plugin versions are migrated to encrypted
storage once during plugin startup and are then removed from the XML
configuration.

### 3. Make the page visible to regular users

**Show in the sidebar for all Jellyfin users** is enabled by default. Reload
already open Jellyfin Web clients after changing this setting. If the Jellyfin
**File Transformation** plugin is installed, its runtime patch is used.
Otherwise, this plugin modifies Jellyfin's `index.html` as a fallback. Another
server restart may therefore be required after a Jellyfin Web update.

The **Requests** item is added to the custom section of the hamburger menu and
is therefore available to all signed-in users. An observer adds it again if
Jellyfin renders a new navigation drawer during navigation.

## Custom Jellyfin repository and automatic updates

The project includes a security-hardened GitHub Actions workflow. Each new
version tag runs tests and dependency audits, creates both ZIP archives,
publishes a GitHub Release, and deploys the current `manifest.json` to GitHub
Pages. Third-party actions are pinned to full commit SHAs, Python test
dependencies are pinned by version and hash, and NuGet dependencies are
restored in locked mode.

One-time setup:

1. Push this directory to a GitHub repository.
2. In **Settings > Pages > Build and deployment**, select **GitHub Actions** as
   the source.
3. In **Settings > Environments > github-pages > Deployment branches and
   tags**, keep **Selected branches and tags** and add a tag rule named `v*`.
   This allows version tags to deploy the repository feed without permitting
   arbitrary refs.
4. Publish version `0.3.0`:

```powershell
git tag -a v0.3.0 -m "MediaForge Requests 0.3.0"
git push origin v0.3.0
```

After the workflow completes successfully, the Jellyfin feed is available at:

```text
https://YOUR-GITHUB-USERNAME.github.io/YOUR-REPOSITORY/manifest.json
```

For a later update, update all version references atomically, review the
changelog, and push the matching tag:

```powershell
$nextVersion = Read-Host "Next version (for example 0.3.1)"
.\scripts\set-version.ps1 -Version $nextVersion
git add .
git commit -m "Release $nextVersion"
git tag -a "v$nextVersion" -m "MediaForge Requests $nextVersion"
git push origin main "v$nextVersion"
```

Jellyfin recognizes the newer version during its next plugin update check by
using the same plugin GUID. The feed intentionally points to the latest release.
Activating a Jellyfin plugin update normally requires a Jellyfin restart. The
separately installed MediaForge companion module is also attached to the
release as a ZIP containing the complete `mediaforge_jellyfin_connector`
directory, but Jellyfin's plugin updater cannot install that module in
MediaForge.

The Jellyfin plugin can also be installed manually. Extract
`dist/MediaForgeRequests_0.3.0.zip` to
`/var/lib/jellyfin/plugins/MediaForgeRequests/`. The destination directory must
contain `Jellyfin.Plugin.MediaForge.dll` and `meta.json`.

## Build and release packages

```powershell
.\scripts\build.ps1
```

If `dotnet` is not available through `PATH`:

```powershell
.\scripts\build.ps1 -DotNet C:\path\to\dotnet.exe
```

The build creates:

- `dist/Jellyfin.Plugin.MediaForge.dll`
- `dist/MediaForgeRequests_0.3.0.zip`
- `dist/mediaforge_jellyfin_connector_0.3.0.zip`
- `dist/SHA256SUMS.txt`

Remove all generated build output, repository manifests, and local test caches
before committing source files:

```powershell
.\scripts\clean.ps1
```

## Security and operation

- Regular users cannot read plugin settings or the MediaForge key.
  Administrator endpoints use Jellyfin's `RequiresElevation` policy.
- The API key is stored with AES-256-GCM encryption. On Unix, the key and
  secret files are additionally restricted to mode `0600`. Backups must include
  both `connector-secret.key` and `mediaforge-api-key.bin`.
- The MediaForge module accepts only MediaForge API keys and requires the
  appropriate scope for each endpoint.
- Catalog URLs are authorized server-side for each Jellyfin user for only
  30 minutes. Modified URLs, URLs authorized for another user, and URLs not
  returned by a MediaForge search are rejected. The MediaForge module also
  validates them against MediaForge's provider registry.
- Search, detail, and download endpoints are rate-limited per user. Payloads,
  response sizes, and field lengths are also restricted.
- The connector never logs API keys or MediaForge response bodies. `X-Api-Key`
  is explicitly redacted from HTTP client logging. External error messages are
  replaced with fixed, non-sensitive messages before they are displayed or
  stored.
- The Jellyfin browser never loads posters directly from third-party sources.
  Jellyfin fetches them server-side through MediaForge's allowlisted and
  SSRF-protected image proxy without exposing either API token in a URL.
- Existing-content decisions are made from Jellyfin's library, not from paths
  or download flags reported by MediaForge. Matching uses provider IDs where
  available and Jellyfin's normalized name/original-title search with
  conservative year and remake handling. The decision is recalculated on the
  server immediately before a request is stored and again when an administrator
  approves a pending request.
- The number of open requests is limited per user.
- During automatic submission or administrator approval, an atomic status
  transition prevents the same request from being processed twice.
- HTTPS is required for connections across host or network boundaries. Use
  unencrypted HTTP only for loopback or a trusted, non-public container network.
  Jellyfin's administration interface should also be available over HTTPS.
- The plugin does not provide media. Only use sources and download content for
  which you have the required rights.

Run the additional security tests with:

```powershell
dotnet run --project .\Tests\Connector.SecurityTests\Connector.SecurityTests.csproj --configuration Release
```

## References

- [Jellyfin-AniWorld-Downloader](https://github.com/SiroxCW/Jellyfin-AniWorld-Downloader)
- [MediaForge](https://github.com/PD-Codes/MediaForge)
