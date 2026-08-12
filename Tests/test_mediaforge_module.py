"""Security regression tests for the MediaForge companion routes."""

from __future__ import annotations

import importlib.util
import sys
import types
import unittest
from functools import wraps
from pathlib import Path

from flask import Flask, jsonify, request


def _mediaforge_login_required(view):
    @wraps(view)
    def decorated(*args, **kwargs):
        if request.headers.get("X-Test-Web-Session") != "present":
            return jsonify({"error": "authentication required"}), 401
        return view(*args, **kwargs)

    return decorated


def _load_routes_module():
    package_names = (
        "mediaforge",
        "mediaforge.web",
        "mediaforge.web.routes",
        "mediaforge.web.thirdparties",
        "mediaforge.web.thirdparties.mediaforge_jellyfin_connector",
        "mediaforge.models",
        "mediaforge.models.common",
    )
    for name in package_names:
        package = types.ModuleType(name)
        package.__path__ = []
        sys.modules[name] = package

    database = types.ModuleType("mediaforge.web.db")
    database.get_setting = lambda _key, default="": default
    database.get_queue_item = lambda queue_id: {
        "id": queue_id,
        "status": "running",
        "current_episode": 1,
        "total_episodes": 4,
        "series_url": "https://secret.invalid/private-title",
        "file_path": "/private/library/file.mkv",
        "errors": "sensitive internal error",
    }
    sys.modules[database.__name__] = database

    common = types.ModuleType("mediaforge.models.common.common")
    common.get_ffmpeg_progress = lambda: {
        "active": True,
        "percent": 50,
        "phase": "download",
        "file": "/private/library/file.mkv",
    }
    sys.modules[common.__name__] = common

    api = types.ModuleType("mediaforge.web.routes.v1_api")

    def check_api_key(scope):
        if request.headers.get("X-Api-Key") != f"{scope}-key":
            return jsonify({"error": "unauthorized"}), 401
        return None

    api._check_api_key = check_api_key
    sys.modules[api.__name__] = api

    auth = types.ModuleType("mediaforge.web.auth")
    auth.login_required = _mediaforge_login_required
    sys.modules[auth.__name__] = auth

    providers = types.ModuleType("mediaforge.providers")

    def resolve_provider(url):
        if not isinstance(url, str) or not url.startswith(
            "https://allowed.invalid/media/"
        ):
            raise ValueError("unsupported")
        return object()

    providers.resolve_provider = resolve_provider
    sys.modules[providers.__name__] = providers

    path = (
        Path(__file__).parents[1]
        / "MediaForge.Module"
        / "mediaforge_jellyfin_connector"
        / "routes.py"
    )
    name = "mediaforge.web.thirdparties.mediaforge_jellyfin_connector.routes"
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def _load_connector_package():
    package_names = (
        "mediaforge",
        "mediaforge.web",
        "mediaforge.web.routes",
        "mediaforge.web.thirdparties",
    )
    for name in package_names:
        package = types.ModuleType(name)
        package.__path__ = []
        sys.modules[name] = package

    registrations = []
    registry = types.ModuleType("mediaforge.web.thirdparties.registry")
    registry.module_setting_key = lambda module_id, key: f"module:{module_id}:{key}"
    registry.register_thirdparty = lambda **kwargs: registrations.append(kwargs)
    sys.modules[registry.__name__] = registry

    routes = types.ModuleType(
        "mediaforge.web.thirdparties.mediaforge_jellyfin_connector.routes"
    )
    routes.create_blueprint = lambda _app, _key: (object(), {"connector.health": "status:read"})
    sys.modules[routes.__name__] = routes

    api = types.ModuleType("mediaforge.web.routes.v1_api")
    api._V1_ENDPOINT_SCOPES = {}
    sys.modules[api.__name__] = api

    path = (
        Path(__file__).parents[1]
        / "MediaForge.Module"
        / "mediaforge_jellyfin_connector"
        / "__init__.py"
    )
    name = "mediaforge.web.thirdparties.mediaforge_jellyfin_connector"
    spec = importlib.util.spec_from_file_location(
        name,
        path,
        submodule_search_locations=[str(path.parent)],
    )
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module, registrations, api._V1_ENDPOINT_SCOPES


class ConnectorRouteSecurityTests(unittest.TestCase):
    def setUp(self):
        routes = _load_routes_module()
        self.app = Flask(__name__)
        self.calls = []
        for endpoint in routes._ROUTE_NAMES.values():
            self.app.add_url_rule(
                f"/internal/{endpoint}",
                endpoint=endpoint,
                view_func=_mediaforge_login_required(self._internal(endpoint)),
                methods=["GET", "POST"],
            )
        self.app.add_url_rule(
            "/internal/home-feed",
            endpoint="api_home_feed",
            view_func=_mediaforge_login_required(
                lambda: jsonify({"rows": {"new": [{"title": "Example"}]}})
            ),
        )
        self.app.add_url_rule(
            "/internal/image",
            endpoint="api_image_proxy",
            view_func=_mediaforge_login_required(lambda: (b"image", 200, {"Content-Type": "image/jpeg"})),
        )
        blueprint, _scopes = routes.create_blueprint(self.app, "connector_enabled")
        self.app.register_blueprint(blueprint)

        # Reproduce MediaForge's blanket session-login pass. The connector
        # must remain usable by a machine client with only X-Api-Key, while
        # every connector view must still enforce its own scoped key.
        for endpoint in _scopes:
            original = self.app.view_functions[endpoint]
            self.app.view_functions[endpoint] = _mediaforge_login_required(original)
        self.client = self.app.test_client()

    def _internal(self, endpoint):
        def handler():
            self.calls.append(endpoint)
            if endpoint == "api_search":
                return jsonify(
                    {
                        "results": [
                            {
                                "title": "Example",
                                "url": "https://allowed.invalid/media/series",
                                "poster_url": "https://allowed.invalid/poster.jpg",
                            }
                        ]
                    }
                )
            if endpoint == "api_series":
                return jsonify(
                    {
                        "title": "Example",
                        "poster_url": "https://allowed.invalid/poster.jpg",
                    }
                )
            if endpoint == "api_episodes":
                return jsonify(
                    {
                        "episodes": [
                            {
                                "url": "https://allowed.invalid/media/movie",
                                "season_number": 1,
                                "downloaded": False,
                            }
                        ]
                    }
                )
            return jsonify({"ok": True})

        return handler

    def test_authentication_is_required(self):
        response = self.client.get("/api/v1/connector/sources")
        self.assertEqual(401, response.status_code)
        self.assertEqual("unauthorized", response.get_json()["error"])
        self.assertEqual([], self.calls)

    def test_valid_api_key_does_not_require_a_mediaforge_web_session(self):
        response = self.client.get(
            "/api/v1/connector/health",
            headers={"X-Api-Key": "status:read-key"},
        )
        self.assertEqual(200, response.status_code)
        self.assertTrue(response.get_json()["ok"])

    def test_discovery_does_not_require_a_mediaforge_web_session(self):
        response = self.client.get(
            "/api/v1/connector/discover",
            headers={"X-Api-Key": "library:read-key"},
        )
        self.assertEqual(200, response.status_code)
        self.assertEqual("Example", response.get_json()["rows"]["new"][0]["title"])

    def test_image_proxy_requires_scope_and_rejects_non_http_urls(self):
        response = self.client.get(
            "/api/v1/connector/image?url=https%3A%2F%2Fallowed.invalid%2Fposter.jpg"
        )
        self.assertEqual(401, response.status_code)

        response = self.client.get(
            "/api/v1/connector/image?url=file%3A%2F%2F%2Fetc%2Fpasswd",
            headers={"X-Api-Key": "library:read-key"},
        )
        self.assertEqual(400, response.status_code)

        response = self.client.get(
            "/api/v1/connector/image?url=https%3A%2F%2Fallowed.invalid%2Fposter.jpg",
            headers={"X-Api-Key": "library:read-key"},
        )
        self.assertEqual(200, response.status_code)
        self.assertEqual("image/jpeg", response.content_type)

    def test_arbitrary_url_is_rejected_before_internal_handler(self):
        response = self.client.get(
            "/api/v1/connector/series?url=http://127.0.0.1/admin",
            headers={"X-Api-Key": "library:read-key"},
        )
        self.assertEqual(400, response.status_code)
        self.assertEqual([], self.calls)

    def test_valid_media_url_reaches_internal_handler(self):
        response = self.client.get(
            "/api/v1/connector/series?url=https://allowed.invalid/media/series",
            headers={"X-Api-Key": "library:read-key"},
        )
        self.assertEqual(200, response.status_code)
        self.assertEqual(["api_series"], self.calls)
        self.assertTrue(response.get_json()["poster_url"].startswith("/api/img?url="))

    def test_search_posters_are_always_rewritten_to_mediaforge_proxy_paths(self):
        response = self.client.post(
            "/api/v1/connector/search",
            json={"keyword": "Example", "site": "aniworld"},
            headers={"X-Api-Key": "library:read-key"},
        )
        self.assertEqual(200, response.status_code)
        poster = response.get_json()["results"][0]["poster_url"]
        self.assertTrue(poster.startswith("/api/img?url="))
        self.assertNotIn("https://allowed.invalid/poster.jpg", poster)

    def test_optional_movie_check_keeps_the_original_result_when_unsupported(self):
        response = self.client.get(
            "/api/v1/connector/episodes?url=https://allowed.invalid/media/movie",
            headers={"X-Api-Key": "library:read-key"},
        )
        self.assertEqual(200, response.status_code)
        self.assertFalse(response.get_json()["episodes"][0]["downloaded"])

    def test_download_rejects_extra_fields_and_injected_episode(self):
        base = {
            "episodes": ["https://allowed.invalid/media/episode-1"],
            "language": "German Dub",
            "provider": "VOE",
            "title": "Title",
            "series_url": "https://allowed.invalid/media/series",
            "upscale": False,
        }
        response = self.client.post(
            "/api/v1/connector/download",
            json={**base, "token": "must-not-be-accepted"},
            headers={"X-Api-Key": "queue:write-key"},
        )
        self.assertEqual(400, response.status_code)

        response = self.client.post(
            "/api/v1/connector/download",
            json={**base, "episodes": ["http://127.0.0.1/admin"]},
            headers={"X-Api-Key": "queue:write-key"},
        )
        self.assertEqual(400, response.status_code)
        self.assertEqual([], self.calls)

    def test_valid_download_reaches_internal_handler(self):
        response = self.client.post(
            "/api/v1/connector/download",
            json={
                "episodes": ["https://allowed.invalid/media/episode-1"],
                "language": "German Dub",
                "provider": "VOE",
                "title": "Title",
                "series_url": "https://allowed.invalid/media/series",
                "upscale": False,
            },
            headers={"X-Api-Key": "queue:write-key"},
        )
        self.assertEqual(200, response.status_code)
        self.assertEqual(["api_download"], self.calls)

    def test_progress_is_scoped_and_contains_no_sensitive_queue_fields(self):
        response = self.client.post(
            "/api/v1/connector/progress",
            json={"queue_ids": [42]},
            headers={"X-Api-Key": "queue:read-key"},
        )
        self.assertEqual(200, response.status_code)
        item = response.get_json()["items"][0]
        self.assertEqual(42, item["queue_id"])
        self.assertEqual(37.5, item["percent"])
        self.assertEqual(
            {"queue_id", "status", "current_episode", "total_episodes", "percent", "phase"},
            set(item),
        )
        self.assertNotIn("file_path", response.get_data(as_text=True))
        self.assertNotIn("series_url", response.get_data(as_text=True))

    def test_progress_rejects_invalid_or_duplicate_ids(self):
        headers = {"X-Api-Key": "queue:read-key"}
        for queue_ids in ([1, 1], [0], [True], ["1"]):
            response = self.client.post(
                "/api/v1/connector/progress",
                json={"queue_ids": queue_ids},
                headers=headers,
            )
            self.assertEqual(400, response.status_code)


class ConnectorRegistrationTests(unittest.TestCase):
    def test_module_registers_an_explicit_module_settings_card(self):
        module, registrations, scopes = _load_connector_package()

        class FakeApp:
            def __init__(self):
                self.blueprints = []

            def register_blueprint(self, blueprint):
                self.blueprints.append(blueprint)

        app = FakeApp()
        module.register(app)

        self.assertEqual(1, len(app.blueprints))
        self.assertEqual(1, len(registrations))
        self.assertEqual("mediaforge_jellyfin_connector", registrations[0]["item_id"])
        self.assertEqual("settings", registrations[0]["settings_host"])
        self.assertEqual("module:mediaforge_jellyfin_connector:enabled", registrations[0]["enabled_setting_key"])
        self.assertEqual({"connector.health": "status:read"}, scopes)


if __name__ == "__main__":
    unittest.main()
