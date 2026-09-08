"""MediaForge companion API for the Jellyfin MediaForge Requests plugin.

The module deliberately reuses MediaForge's own route handlers.  That keeps
provider support, source toggles, language handling and queue validation in
one place instead of slowly reimplementing MediaForge in a second project.
"""

import inspect

from ..registry import module_setting_key, register_thirdparty
from .routes import create_blueprint

MODULE_NAME = "Jellyfin Connector"
MODULE_DESCRIPTION = (
    "Authenticated search, metadata and queue endpoints for the "
    "Jellyfin MediaForge Requests plugin."
)
MODULE_DESCRIPTION_DE = (
    "Authentifizierte Such-, Metadaten- und Warteschlangen-Endpunkte für "
    "das Jellyfin-Plugin MediaForge Requests."
)
MODULE_AUTHOR = "MediaForge Jellyfin Connector contributors"
MODULE_ENABLED_DEFAULT = True
MODULE_VERSION = "0.5.0"
MODULE_API_VERSION = 1
MODULE_MIN_APP_VERSION = "1.5.0"
MODULE_MAX_APP_VERSION = "1.6.999"
MODULE_REQUIREMENTS = ()
MODULE_ID = "mediaforge_jellyfin_connector"
MODULE_HOMEPAGE = ""
MODULE_LICENSE = "GPL-3.0-or-later"

_SETTING_KEY = module_setting_key(MODULE_ID, "enabled")


def register(app) -> None:
    """Register the connector routes before MediaForge applies auth wrappers."""
    blueprint, endpoint_scopes = create_blueprint(app, _SETTING_KEY, MODULE_VERSION)
    app.register_blueprint(blueprint)

    # MediaForge 1.6+ owns and validates module scopes through its public
    # registry. Keep the narrow fallback because this module deliberately
    # retains MediaForge 1.5.0 as its minimum supported application version.
    try:
        from ...routes.v1_api import register_v1_endpoint_scopes
    except ImportError:  # MediaForge 1.5 compatibility
        from ...routes.v1_api import _V1_ENDPOINT_SCOPES

        _V1_ENDPOINT_SCOPES.update(endpoint_scopes)
    else:
        register_v1_endpoint_scopes(
            MODULE_ID,
            endpoint_scopes,
            blueprint="mediaforge_jellyfin_connector",
        )

    registration = {
        "item_id": MODULE_ID,
        "label": MODULE_NAME,
        "enabled_setting_key": _SETTING_KEY,
        "description": MODULE_DESCRIPTION,
        "enable_label": "Enable Jellyfin Connector",
        "enable_desc": (
            "Allows a Jellyfin server with a scoped MediaForge API key to "
            "search the enabled sources and add approved downloads to the queue."
        ),
        "badges": [("API", "#00a4dc"), ("Jellyfin", "#8b5cf6")],
        "section": "system",
        "settings_host": "settings",
    }
    if "blueprint" in inspect.signature(register_thirdparty).parameters:
        registration["blueprint"] = "mediaforge_jellyfin_connector"
    register_thirdparty(**registration)
