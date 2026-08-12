# MediaForge Module: Jellyfin Connector

This companion module adds API-key-protected endpoints to MediaForge for
searching, resolving titles, seasons, and episodes, and queueing downloads. It
uses the same handlers as the MediaForge Web UI internally, so the sources,
providers, and download settings enabled in MediaForge automatically apply.

## Installation

1. Copy the `mediaforge_jellyfin_connector` directory to
   `~/.mediaforge/thirdparties/mediaforge_jellyfin_connector`.
2. Restart MediaForge.
3. In MediaForge, open **Module Manager > Module Settings** and confirm that
   **Jellyfin Connector** is enabled.
4. Under **Settings > API**, create a key with these scopes: `status:read`,
   `library:read`, `queue:read`, and `queue:write`.
5. Enter the MediaForge URL and the key, which is displayed only once, in the
   Jellyfin plugin settings.

The connector intentionally has no separate page of its own. Its only
module-specific setting is the **Enable Jellyfin Connector** toggle under
**Module Manager > Module Settings**. API keys are managed centrally by
MediaForge under **Settings > API** and are not stored in this module.

If the module card is missing, verify the exact directory layout:

```text
~/.mediaforge/thirdparties/mediaforge_jellyfin_connector/__init__.py
~/.mediaforge/thirdparties/mediaforge_jellyfin_connector/routes.py
```

There must not be a second nested `mediaforge_jellyfin_connector` directory.
After correcting a manual installation, restart MediaForge or use the Module
Manager's refresh function. The MediaForge log will report an import or
compatibility error if registration still fails.

After saving, the key is stored in encrypted form by the Jellyfin plugin and is
never returned to a browser. The module validates every submitted media URL
against MediaForge's own provider registry, limits field lengths, and accepts
only the expected JSON fields when queueing a download.
