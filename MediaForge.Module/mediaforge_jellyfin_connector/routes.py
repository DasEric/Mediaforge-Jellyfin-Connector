"""API-key protected bridge to MediaForge's own search and queue handlers."""

from __future__ import annotations

from urllib.parse import quote, urlsplit

from flask import Blueprint, current_app, jsonify, request

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


def _proxy_poster(payload: dict) -> None:
    poster = payload.get("poster_url")
    if isinstance(poster, str) and poster.startswith(("http://", "https://")):
        payload["poster_url"] = "/api/img?url=" + quote(poster, safe="")


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
                "version": "0.2.7",
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
        upstream = current_app.make_response(internal["search"]())
        if upstream.status_code != 200:
            return upstream
        payload = upstream.get_json(silent=True)
        if not isinstance(payload, dict) or not isinstance(payload.get("results"), list):
            return jsonify({"error": "invalid search response"}), 502
        for item in payload["results"]:
            if not isinstance(item, dict):
                continue
            _proxy_poster(item)
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
        payload = upstream.get_json(silent=True)
        if not isinstance(payload, dict) or not isinstance(payload.get("episodes"), list):
            return jsonify({"error": "invalid episodes response"}), 502

        # MediaForge 1.5 reports movie entries as not downloaded even when the
        # target file already exists. Its provider models do expose the real
        # check, so correct only the explicit single-movie response shape.
        episodes = payload["episodes"]
        if len(episodes) == 1 and isinstance(episodes[0], dict) and "season_number" in episodes[0]:
            try:
                provider = resolve_provider(request.args.get("url", "").strip())
                model = provider.episode_cls(url=request.args["url"].strip())
                state = model.is_downloaded
                episodes[0]["downloaded"] = (
                    bool(state.get("exists")) if isinstance(state, dict) else bool(state)
                )
            except Exception:  # noqa: BLE001 - optional compatibility correction
                # Fail closed to MediaForge's original result. Queue workers
                # perform their own final existence check as well.
                episodes[0].setdefault("downloaded", False)
        return jsonify(payload)

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

    @bp.get("/api/v1/connector/discover")
    def api_connector_discover():
        auth_error = guard("library:read")
        if auth_error:
            return auth_error
        # The MediaForge home feed accepts adult/limit query parameters. The
        # connector deliberately exposes neither: adult content remains an
        # explicit Jellyfin administrator decision, and MediaForge's bounded
        # configured row limit is used as-is.
        if request.args:
            return jsonify({"error": "unexpected query parameters"}), 400
        handler = late_internal("api_home_feed")
        return handler() if handler is not None else (jsonify({"error": "home feed unavailable"}), 503)

    @bp.get("/api/v1/connector/image")
    def api_connector_image():
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
        # MediaForge's own image handler performs the authoritative hostname
        # allowlist and DNS/IP SSRF checks before any network request.
        handler = late_internal("api_image_proxy")
        return handler() if handler is not None else (jsonify({"error": "image proxy unavailable"}), 503)

    # MediaForge's current startup pass reads ``_V1_ENDPOINT_SCOPES`` and
    # correctly leaves these views free of its session-login wrapper.  A
    # module installed or refreshed in an already running MediaForge process,
    # however, can still be wrapped by older/current live-registration paths.
    # Such a wrapper rejects a valid X-Api-Key unless the caller also happens
    # to own a browser session, which machine clients such as Jellyfin do not.
    #
    # Blueprint before-request handlers run after MediaForge's application
    # before-request security checks but before Flask resolves the view for
    # dispatch.  Restore only our exact, locally captured view when a wrapper
    # chain still terminates at that view.  The route's first operation remains
    # ``guard()`` / ``_check_api_key()``, and no MediaForge or third-party
    # endpoint can be affected by this narrowly keyed compatibility fallback.
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

    @bp.before_request
    def _keep_connector_api_key_only():
        endpoint = request.endpoint or ""
        expected = connector_views.get(endpoint)
        registered = current_app.view_functions.get(endpoint)
        if expected is None or registered is None or registered is expected:
            return

        candidate = registered
        visited = set()
        while candidate is not expected and id(candidate) not in visited:
            visited.add(id(candidate))
            candidate = getattr(candidate, "__wrapped__", None)
            if candidate is None:
                break

        if candidate is expected:
            current_app.view_functions[endpoint] = expected

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
