"""API-key protected bridge to MediaForge's own search and queue handlers."""

from __future__ import annotations

from flask import Blueprint, jsonify, request

from ....models.common.common import get_ffmpeg_progress
from ....providers import resolve_provider
from ...db import get_queue_item, get_setting
from ...routes.v1_api import _check_api_key

_ROUTE_NAMES = {
    "sources": "api_search_sources",
    "search": "api_search",
    "series": "api_series",
    "seasons": "api_seasons",
    "episodes": "api_episodes",
    "providers": "api_providers",
    "download": "api_download",
}

_MAX_EPISODES = 500
_MAX_URL_LENGTH = 2048
_MAX_PROGRESS_IDS = 200
_QUEUE_STATES = {"queued", "running", "completed", "partial", "failed", "cancelled"}
_PROGRESS_PHASES = {"download", "ffmpeg", "upscaling", "move"}


def _safe_text(value, maximum: int) -> bool:
    return (
        isinstance(value, str)
        and 0 < len(value.strip()) <= maximum
        and not any(ord(character) < 32 or ord(character) == 127 for character in value)
    )


def _is_mediaforge_url(value) -> bool:
    if not _safe_text(value, _MAX_URL_LENGTH):
        return False
    try:
        resolve_provider(value.strip())
        return True
    except (TypeError, ValueError):
        return False


def _validate_url_argument():
    value = request.args.get("url", "")
    if not _is_mediaforge_url(value):
        return jsonify({"error": "unsupported media URL"}), 400
    return None


def _bounded_number(value, minimum: float, maximum: float) -> float:
    try:
        return max(minimum, min(maximum, float(value)))
    except (TypeError, ValueError):
        return minimum


def _safe_progress_item(queue_id: int, item, live_progress):
    """Return only non-sensitive progress fields for a single queue item."""
    if not isinstance(item, dict):
        return None

    status = item.get("status")
    if status not in _QUEUE_STATES:
        status = "unknown"
    total = int(_bounded_number(item.get("total_episodes"), 0, _MAX_EPISODES))
    current = int(_bounded_number(item.get("current_episode"), 0, total or _MAX_EPISODES))
    phase = "download"
    active_percent = 0.0
    if status == "running" and isinstance(live_progress, dict) and live_progress.get("active"):
        candidate_phase = live_progress.get("phase")
        if candidate_phase in _PROGRESS_PHASES:
            phase = candidate_phase
        active_percent = _bounded_number(live_progress.get("percent"), 0, 100)

    if status == "completed":
        overall = 100.0
    elif total > 0:
        overall = min(100.0, ((current + active_percent / 100.0) / total) * 100.0)
    else:
        overall = active_percent

    return {
        "queue_id": queue_id,
        "status": status,
        "current_episode": current,
        "total_episodes": total,
        "percent": round(overall, 1),
        "phase": phase,
    }


def create_blueprint(app, enabled_setting_key: str):
    """Create the connector blueprint and return its endpoint/scope map.

    MediaForge registers the internal search and queue routes before it
    discovers third-party modules.  Capturing the view functions here means
    we call the same implementation before the application's later blanket
    session-login wrapper is applied.  The connector endpoints have their own
    `_check_api_key` gate and are inserted into MediaForge's v1 scope map.
    """

    missing = [name for name in _ROUTE_NAMES.values() if name not in app.view_functions]
    if missing:
        raise RuntimeError(
            "Jellyfin Connector is incompatible with this MediaForge build; "
            "missing routes: " + ", ".join(missing)
        )

    internal = {key: app.view_functions[name] for key, name in _ROUTE_NAMES.items()}

    bp = Blueprint("mediaforge_jellyfin_connector", __name__)

    def guard(scope: str):
        if get_setting(enabled_setting_key, "1") != "1":
            return jsonify({"error": "connector disabled"}), 503
        return _check_api_key(scope)

    @bp.get("/api/v1/connector/health")
    def api_connector_health():
        auth_error = guard("status:read")
        if auth_error:
            return auth_error
        return jsonify(
            {
                "ok": True,
                "module": "mediaforge_jellyfin_connector",
                "version": "0.2.2",
            }
        )

    @bp.get("/api/v1/connector/sources")
    def api_connector_sources():
        auth_error = guard("library:read")
        if auth_error:
            return auth_error
        return internal["sources"]()

    @bp.post("/api/v1/connector/search")
    def api_connector_search():
        auth_error = guard("library:read")
        if auth_error:
            return auth_error
        body = request.get_json(silent=True)
        if not isinstance(body, dict):
            return jsonify({"error": "JSON object required"}), 400
        if set(body) - {"keyword", "site"}:
            return jsonify({"error": "unexpected request fields"}), 400
        if not _safe_text(body.get("keyword"), 120) or len(body["keyword"].strip()) < 2:
            return jsonify({"error": "invalid keyword"}), 400
        if not _safe_text(body.get("site"), 80):
            return jsonify({"error": "invalid source"}), 400
        return internal["search"]()

    @bp.get("/api/v1/connector/series")
    def api_connector_series():
        auth_error = guard("library:read")
        if auth_error:
            return auth_error
        validation_error = _validate_url_argument()
        if validation_error:
            return validation_error
        return internal["series"]()

    @bp.get("/api/v1/connector/seasons")
    def api_connector_seasons():
        auth_error = guard("library:read")
        if auth_error:
            return auth_error
        validation_error = _validate_url_argument()
        if validation_error:
            return validation_error
        return internal["seasons"]()

    @bp.get("/api/v1/connector/episodes")
    def api_connector_episodes():
        auth_error = guard("library:read")
        if auth_error:
            return auth_error
        validation_error = _validate_url_argument()
        if validation_error:
            return validation_error
        return internal["episodes"]()

    @bp.get("/api/v1/connector/providers")
    def api_connector_providers():
        auth_error = guard("library:read")
        if auth_error:
            return auth_error
        validation_error = _validate_url_argument()
        if validation_error:
            return validation_error
        return internal["providers"]()

    @bp.post("/api/v1/connector/download")
    def api_connector_download():
        auth_error = guard("queue:write")
        if auth_error:
            return auth_error

        body = request.get_json(silent=True)
        if not isinstance(body, dict):
            return jsonify({"error": "JSON object required"}), 400
        if set(body) - {
            "episodes",
            "language",
            "provider",
            "title",
            "series_url",
            "upscale",
        }:
            return jsonify({"error": "unexpected request fields"}), 400

        episodes = body.get("episodes")
        if (
            not isinstance(episodes, list)
            or not 1 <= len(episodes) <= _MAX_EPISODES
            or any(not _is_mediaforge_url(url) for url in episodes)
            or len(set(episodes)) != len(episodes)
        ):
            return jsonify({"error": "invalid episodes"}), 400
        if not _is_mediaforge_url(body.get("series_url")):
            return jsonify({"error": "invalid series URL"}), 400
        if not _safe_text(body.get("title"), 300):
            return jsonify({"error": "invalid title"}), 400
        if not _safe_text(body.get("language"), 100):
            return jsonify({"error": "invalid language"}), 400
        if not _safe_text(body.get("provider"), 100):
            return jsonify({"error": "invalid provider"}), 400
        if "upscale" in body and not isinstance(body["upscale"], bool):
            return jsonify({"error": "invalid upscale flag"}), 400
        return internal["download"]()

    @bp.post("/api/v1/connector/progress")
    def api_connector_progress():
        auth_error = guard("queue:read")
        if auth_error:
            return auth_error
        body = request.get_json(silent=True)
        if not isinstance(body, dict) or set(body) != {"queue_ids"}:
            return jsonify({"error": "queue_ids JSON field required"}), 400
        queue_ids = body.get("queue_ids")
        if (
            not isinstance(queue_ids, list)
            or not 1 <= len(queue_ids) <= _MAX_PROGRESS_IDS
            or any(type(queue_id) is not int or queue_id <= 0 for queue_id in queue_ids)
            or len(set(queue_ids)) != len(queue_ids)
        ):
            return jsonify({"error": "invalid queue ids"}), 400

        live_progress = get_ffmpeg_progress()
        items = []
        for queue_id in queue_ids:
            progress = _safe_progress_item(queue_id, get_queue_item(queue_id), live_progress)
            if progress is not None:
                items.append(progress)
        return jsonify({"items": items})

    scopes = {
        "mediaforge_jellyfin_connector.api_connector_health": "status:read",
        "mediaforge_jellyfin_connector.api_connector_sources": "library:read",
        "mediaforge_jellyfin_connector.api_connector_search": "library:read",
        "mediaforge_jellyfin_connector.api_connector_series": "library:read",
        "mediaforge_jellyfin_connector.api_connector_seasons": "library:read",
        "mediaforge_jellyfin_connector.api_connector_episodes": "library:read",
        "mediaforge_jellyfin_connector.api_connector_providers": "library:read",
        "mediaforge_jellyfin_connector.api_connector_download": "queue:write",
        "mediaforge_jellyfin_connector.api_connector_progress": "queue:read",
    }
    return bp, scopes
