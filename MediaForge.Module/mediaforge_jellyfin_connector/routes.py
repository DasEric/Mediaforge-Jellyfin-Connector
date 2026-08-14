"""API-key protected bridge to MediaForge's own search and queue handlers."""

from __future__ import annotations

import json
from urllib.parse import parse_qs, quote, urlsplit

from flask import Blueprint, current_app, jsonify, request

from ....models.common.common import get_ffmpeg_progress
from ....providers import resolve_provider
from ...db import get_queue_item, get_setting

try:
    from ...routes.v1_api import check_api_key
except ImportError:  # MediaForge 1.5 compatibility
    from ...routes.v1_api import _check_api_key as check_api_key

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
_PROGRESS_PHASES = {"download", "ffmpeg"}


def _without_mediaforge_session_login(view):
    """Remove only MediaForge's own ``login_required`` wrapper.

    During normal startup third-party modules are registered before the
    blanket session-auth pass, so the internal handlers captured below are
    raw views.  During a live module install/refresh those handlers have
    already been wrapped.  Jellyfin is a machine client and has no MediaForge
    browser session; the connector supplies the replacement security boundary
    through its scoped API-key guard.

    Compare code objects produced by MediaForge's decorator instead of blindly
    following every ``__wrapped__`` link.  This deliberately leaves admin,
    age-gate, and any future unrelated security decorators intact.
    """
    from ...auth import login_required

    def probe():
        pass

    login_wrapper_code = login_required(probe).__code__
    candidate = view
    visited = set()
    while (
        id(candidate) not in visited
        and getattr(candidate, "__code__", None) is login_wrapper_code
    ):
        visited.add(id(candidate))
        wrapped = getattr(candidate, "__wrapped__", None)
        if wrapped is None:
            break
        candidate = wrapped
    return candidate


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


def _normalize_poster_path(poster: str) -> str:
    """Return one canonical MediaForge image-proxy path or an empty value."""
    if not _safe_text(poster, 4096):
        return ""
    try:
        parsed = urlsplit(poster.strip())
    except ValueError:
        return ""

    raw_url = ""
    if parsed.scheme in {"http", "https"}:
        if not parsed.hostname or parsed.username or parsed.password or parsed.fragment:
            return ""
        raw_url = poster.strip()
    elif (
        not parsed.scheme
        and not parsed.netloc
        and parsed.path == "/api/img"
        and not parsed.fragment
    ):
        try:
            values = parse_qs(parsed.query, keep_blank_values=True, strict_parsing=True)
        except ValueError:
            return ""
        if set(values) != {"url"} or len(values["url"]) != 1:
            return ""
        raw_url = values["url"][0].strip()
        try:
            upstream = urlsplit(raw_url)
        except ValueError:
            return ""
        if (
            upstream.scheme not in {"http", "https"}
            or not upstream.hostname
            or upstream.username
            or upstream.password
            or upstream.fragment
        ):
            return ""
    else:
        return ""
    if not _safe_text(raw_url, _MAX_URL_LENGTH):
        return ""
    return "/api/img?url=" + quote(raw_url, safe="")


def _proxy_poster(payload: dict) -> None:
    if "poster_url" not in payload:
        return
    poster = payload.get("poster_url")
    payload["poster_url"] = _normalize_poster_path(poster) if isinstance(poster, str) else ""


def _proxy_posters(value) -> None:
    if isinstance(value, dict):
        _proxy_poster(value)
        for child in value.values():
            _proxy_posters(child)
    elif isinstance(value, list):
        for child in value:
            _proxy_posters(child)


def _read_source_policy(response):
    payload = response.get_json(silent=True)
    if not isinstance(payload, dict) or not isinstance(payload.get("sources"), list):
        return None
    sources = []
    seen_source_ids = set()
    for item in payload["sources"]:
        source_id = item.get("id") if isinstance(item, dict) else None
        source_key = source_id.casefold() if isinstance(source_id, str) else ""
        if (
            isinstance(item, dict)
            and _safe_text(source_id, 80)
            and source_key not in seen_source_ids
            and isinstance(item.get("adult"), bool)
            and isinstance(item.get("enabled", True), bool)
        ):
            seen_source_ids.add(source_key)
            sources.append(item)
    payload["sources"] = sources
    return payload


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


def _accepted_episode_count(queue_id: int):
    """Read only the number of episode URLs persisted by MediaForge."""
    try:
        item = get_queue_item(queue_id)
        if not isinstance(item, dict):
            return None
        episodes = item.get("episodes")
        if isinstance(episodes, str):
            episodes = json.loads(episodes)
        if not isinstance(episodes, list) or not all(
            isinstance(value, str) for value in episodes
        ):
            return None
        return len(episodes)
    except Exception as exc:  # noqa: BLE001 - optional post-insert diagnostic
        current_app.logger.warning(
            "MediaForge connector could not verify the queued episode count (%s)",
            type(exc).__name__,
        )
        return None


def create_blueprint(app, enabled_setting_key: str, module_version: str = "unknown"):
    """Create the connector blueprint and return its endpoint/scope map.

    MediaForge registers the internal search and queue routes before it
    discovers third-party modules.  Capturing the view functions here means
    we call the same implementation before the application's later blanket
    session-login wrapper is applied.  The connector endpoints have their own
    `check_api_key` gate and are inserted into MediaForge's v1 scope map.
    """

    missing = [name for name in _ROUTE_NAMES.values() if name not in app.view_functions]
    if missing:
        raise RuntimeError(
            "Jellyfin Connector is incompatible with this MediaForge build; "
            "missing routes: " + ", ".join(missing)
        )

    internal = {
        key: _without_mediaforge_session_login(app.view_functions[name])
        for key, name in _ROUTE_NAMES.items()
    }

    def late_internal(endpoint: str):
        # MediaForge registers browse/image routes after discovering modules.
        # Resolve these two handlers only when a request arrives, by which time
        # application startup is complete. This also works for a live module
        # install, where the handlers already exist.
        view = current_app.view_functions.get(endpoint)
        if view is None:
            return None
        return _without_mediaforge_session_login(view)

    bp = Blueprint("mediaforge_jellyfin_connector", __name__)

    def guard(scope: str):
        if get_setting(enabled_setting_key, "1") != "1":
            return jsonify({"error": "connector disabled"}), 503
        return check_api_key(scope)

    @bp.get("/api/v1/connector/health")
    def api_connector_health():
        auth_error = guard("status:read")
        if auth_error:
            return auth_error
        return jsonify(
            {
                "ok": True,
                "module": "mediaforge_jellyfin_connector",
                "version": module_version,
            }
        )

    @bp.get("/api/v1/connector/sources")
    def api_connector_sources():
        auth_error = guard("library:read")
        if auth_error:
            return auth_error
        if request.args:
            return jsonify({"error": "unexpected query parameters"}), 400
        upstream = current_app.make_response(internal["sources"]())
        if upstream.status_code != 200:
            return upstream
        payload = _read_source_policy(upstream)
        if payload is None:
            return jsonify({"error": "invalid sources response"}), 502
        # MediaForge's age gate is authoritative. API-key requests have no
        # browser session and therefore cannot opt into adult sources.
        payload["sources"] = [
            item
            for item in payload["sources"]
            if not item["adult"] and item.get("enabled", True) is not False
        ]
        visible_ids = {item["id"] for item in payload["sources"]}
        if isinstance(payload.get("order"), list):
            payload["order"] = [source_id for source_id in payload["order"] if source_id in visible_ids]
        return jsonify(payload)

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
        sources_response = current_app.make_response(internal["sources"]())
        if sources_response.status_code != 200:
            return sources_response
        source_policy = _read_source_policy(sources_response)
        if source_policy is None:
            return jsonify({"error": "invalid sources response"}), 502
        source = next(
            (item for item in source_policy["sources"] if item["id"] == body["site"].strip()),
            None,
        )
        if source is None:
            return jsonify({"error": "unknown source"}), 400
        if source["adult"]:
            return jsonify({"error": "adult source not permitted", "code": "age_limited"}), 403
        if source.get("enabled", True) is False:
            return jsonify({"error": "source disabled"}), 400

        upstream = current_app.make_response(internal["search"]())
        if upstream.status_code != 200:
            return upstream
        payload = upstream.get_json(silent=True)
        if not isinstance(payload, dict) or not isinstance(payload.get("results"), list):
            return jsonify({"error": "invalid search response"}), 502
        _proxy_posters(payload["results"])
        return jsonify(payload)

    @bp.get("/api/v1/connector/series")
    def api_connector_series():
        auth_error = guard("library:read")
        if auth_error:
            return auth_error
        validation_error = _validate_url_argument()
        if validation_error:
            return validation_error
        upstream = current_app.make_response(internal["series"]())
        if upstream.status_code != 200:
            return upstream
        payload = upstream.get_json(silent=True)
        if not isinstance(payload, dict):
            return jsonify({"error": "invalid series response"}), 502
        _proxy_poster(payload)
        return jsonify(payload)

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
        upstream = current_app.make_response(internal["episodes"]())
        if upstream.status_code != 200:
            return upstream
        return upstream

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
        upstream = current_app.make_response(internal["download"]())
        if upstream.status_code != 200:
            return upstream
        payload = upstream.get_json(silent=True)
        if not isinstance(payload, dict):
            return upstream
        queue_id = payload.get("queue_id")
        if isinstance(queue_id, str) and queue_id.isdecimal():
            queue_id = int(queue_id)
        if isinstance(queue_id, int) and not isinstance(queue_id, bool) and queue_id > 0:
            accepted_count = _accepted_episode_count(queue_id)
            if accepted_count is not None:
                payload["accepted_episode_count"] = accepted_count
        return jsonify(payload)

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

    @bp.get("/api/v1/connector/discover")
    def api_connector_discover():
        auth_error = guard("library:read")
        if auth_error:
            return auth_error
        # The MediaForge home feed accepts adult/limit query parameters. The
        # connector deliberately exposes neither: API-key clients cannot opt
        # into adult content, and MediaForge's bounded configured row limit is
        # used as-is.
        if request.args:
            return jsonify({"error": "unexpected query parameters"}), 400
        sources_response = current_app.make_response(internal["sources"]())
        if sources_response.status_code != 200:
            return sources_response
        source_policy = _read_source_policy(sources_response)
        if source_policy is None:
            return jsonify({"error": "invalid sources response"}), 502
        visible_ids = {
            item["id"]
            for item in source_policy["sources"]
            if not item["adult"] and item.get("enabled", True) is not False
        }
        handler = late_internal("api_home_feed")
        if handler is None:
            return jsonify({"error": "home feed unavailable"}), 503
        upstream = current_app.make_response(handler())
        if upstream.status_code != 200:
            return upstream
        payload = upstream.get_json(silent=True)
        if (
            not isinstance(payload, dict)
            or not isinstance(payload.get("rows"), dict)
        ):
            return jsonify({"error": "invalid home feed response"}), 502
        for row_name, items in list(payload["rows"].items()):
            payload["rows"][row_name] = (
                [
                    item
                    for item in items
                    if isinstance(item, dict) and item.get("source") in visible_ids
                ]
                if isinstance(items, list)
                else []
            )
        _proxy_posters(payload)
        return jsonify(payload)

    @bp.get("/api/v1/connector/image")
    def api_connector_image():
        """Compatibility image proxy for MediaForge 1.5 API-key clients."""
        auth_error = guard("library:read")
        if auth_error:
            return auth_error
        if set(request.args) != {"url"}:
            return jsonify({"error": "url query field required"}), 400
        raw_url = request.args.get("url", "").strip()
        if not _safe_text(raw_url, _MAX_URL_LENGTH):
            return jsonify({"error": "invalid image URL"}), 400
        try:
            parsed = urlsplit(raw_url)
        except ValueError:
            return jsonify({"error": "invalid image URL"}), 400
        if (
            parsed.scheme not in {"http", "https"}
            or not parsed.hostname
            or parsed.username is not None
            or parsed.password is not None
            or parsed.fragment
        ):
            return jsonify({"error": "invalid image URL"}), 400
        handler = late_internal("api_image_proxy")
        return handler() if handler is not None else (jsonify({"error": "image proxy unavailable"}), 503)

    connector_views = {
        "mediaforge_jellyfin_connector.api_connector_health": api_connector_health,
        "mediaforge_jellyfin_connector.api_connector_sources": api_connector_sources,
        "mediaforge_jellyfin_connector.api_connector_search": api_connector_search,
        "mediaforge_jellyfin_connector.api_connector_series": api_connector_series,
        "mediaforge_jellyfin_connector.api_connector_seasons": api_connector_seasons,
        "mediaforge_jellyfin_connector.api_connector_episodes": api_connector_episodes,
        "mediaforge_jellyfin_connector.api_connector_providers": api_connector_providers,
        "mediaforge_jellyfin_connector.api_connector_download": api_connector_download,
        "mediaforge_jellyfin_connector.api_connector_progress": api_connector_progress,
        "mediaforge_jellyfin_connector.api_connector_discover": api_connector_discover,
        "mediaforge_jellyfin_connector.api_connector_image": api_connector_image,
    }

    scopes = dict.fromkeys(connector_views)
    scopes.update(
        {
            "mediaforge_jellyfin_connector.api_connector_health": "status:read",
            "mediaforge_jellyfin_connector.api_connector_sources": "library:read",
            "mediaforge_jellyfin_connector.api_connector_search": "library:read",
            "mediaforge_jellyfin_connector.api_connector_series": "library:read",
            "mediaforge_jellyfin_connector.api_connector_seasons": "library:read",
            "mediaforge_jellyfin_connector.api_connector_episodes": "library:read",
            "mediaforge_jellyfin_connector.api_connector_providers": "library:read",
            "mediaforge_jellyfin_connector.api_connector_download": "queue:write",
            "mediaforge_jellyfin_connector.api_connector_progress": "queue:read",
            "mediaforge_jellyfin_connector.api_connector_discover": "library:read",
            "mediaforge_jellyfin_connector.api_connector_image": "library:read",
        }
    )
    return bp, scopes
