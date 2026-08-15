"""Security regression tests for the MediaForge companion routes."""

from __future__ import annotations

import importlib.util
import json
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


def _load_routes_module(*, modern: bool = True):
    package_names = (
        "mediaforge",
        "mediaforge.web",
        "mediaforge.web.routes",
        "mediaforge.web.thirdparties",
        "mediaforge.web.thirdparties.mediaforge_jellyfin_connector",
        "mediaforge.mirrors",
        "mediaforge.models",
        "mediaforge.models.common",
    )
    for name in package_names:
        package = types.ModuleType(name)
        package.__path__ = []
        sys.modules[name] = package

    database = types.ModuleType("mediaforge.web.db")
    database.get_setting = lambda _key, default="": default
    database.get_custom_paths = lambda: [
        {
            "id": 11,
            "name": "Series",
            "path": "/private/series",
            "default_sites": "aniworld,sto",
        },
        {
            "id": 12,
            "name": "Movies",
            "path": "/private/movies",
            "default_sites": "filmpalast,filmo",
        },
    ]
    database.get_queue_item = lambda queue_id: {
        "id": queue_id,
        "status": "running",
        "current_episode": 1,
        "total_episodes": 4,
        "series_url": "https://secret.invalid/private-title",
        "file_path": "/private/library/file.mkv",
        "errors": "sensitive internal error",
        "episodes": json.dumps(
            (request.get_json(silent=True) or {}).get("episodes", [])
        ),
    }
    sys.modules[database.__name__] = database

    mirrors = types.ModuleType("mediaforge.mirrors")
    mirrors.site_for_url = lambda url: (
        "filmpalast" if isinstance(url, str) and url.endswith("/movie") else "aniworld"
    )
    sys.modules[mirrors.__name__] = mirrors

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

    if modern:
        api.check_api_key = check_api_key
    else:
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


def _load_connector_package(*, modern: bool = True):
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
    scope_registrations = []
    registry = types.ModuleType("mediaforge.web.thirdparties.registry")
    registry.module_setting_key = lambda module_id, key: f"module:{module_id}:{key}"

    if modern:

        def register_thirdparty(*, blueprint=None, **kwargs):
            registrations.append({**kwargs, "blueprint": blueprint})

    else:

        def register_thirdparty(**kwargs):
            registrations.append(kwargs)

    registry.register_thirdparty = register_thirdparty
    sys.modules[registry.__name__] = registry

    routes = types.ModuleType(
        "mediaforge.web.thirdparties.mediaforge_jellyfin_connector.routes"
    )
    routes.create_blueprint = lambda _app, _key, _version: (
        object(),
        {"mediaforge_jellyfin_connector.connector_health": "status:read"},
    )
    sys.modules[routes.__name__] = routes

    api = types.ModuleType("mediaforge.web.routes.v1_api")
    api._V1_ENDPOINT_SCOPES = {}
    if modern:
        api.register_v1_endpoint_scopes = lambda item_id, mapping, **kwargs: scope_registrations.append(
            (item_id, dict(mapping), kwargs)
        ) or dict(mapping)
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
    return module, registrations, scope_registrations, api._V1_ENDPOINT_SCOPES


class ConnectorRouteSecurityTests(unittest.TestCase):
    def setUp(self):
        routes = _load_routes_module()
        self.routes = routes
        self.app = Flask(__name__)
        self.calls = []
        self.download_bodies = []
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
                lambda: jsonify(
                    {
                        "rows": {
                            "new": [
                                {
                                    "title": "Example",
                                    "source": "aniworld",
                                    "poster_url": "/api/img?url=https%3A%2F%2Fallowed.invalid%2Fposter.jpg",
                                },
                                {
                                    "title": "Adult",
                                    "source": "hanime",
                                    "poster_url": "https://allowed.invalid/adult.jpg",
                                },
                                {
                                    "title": "Disabled",
                                    "source": "disabled",
                                    "poster_url": "https://allowed.invalid/disabled.jpg",
                                },
                            ]
                        }
                    }
                )
            ),
        )
        self.app.add_url_rule(
            "/internal/image",
            endpoint="api_image_proxy",
            view_func=_mediaforge_login_required(lambda: (b"image", 200, {"Content-Type": "image/jpeg"})),
        )
        blueprint, _scopes = routes.create_blueprint(
            self.app, "connector_enabled", "0.3.0"
        )
        self.app.register_blueprint(blueprint)

        self.client = self.app.test_client()

    def _internal(self, endpoint):
        def handler():
            self.calls.append(endpoint)
            if endpoint == "api_search_sources":
                return jsonify(
                    {
                        "sources": [
                            {"id": "aniworld", "label": "AniWorld", "adult": False},
                            {"id": "ANIWORLD", "label": "Duplicate", "adult": False},
                            {"id": "hanime", "label": "hanime 18+", "adult": True},
                            {
                                "id": "disabled",
                                "label": "Disabled",
                                "adult": False,
                                "enabled": False,
                            },
                            {
                                "id": "malformed",
                                "label": "Malformed",
                                "adult": False,
                                "enabled": "1",
                            },
                        ],
                        "order": ["hanime", "disabled", "malformed", "aniworld"],
                    }
                )
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
            if endpoint == "api_download":
                self.download_bodies.append(dict(request.get_json(silent=True) or {}))
                return jsonify({"queue_id": 42})
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
        self.assertEqual("0.3.0", response.get_json()["version"])

    def test_mediaforge_15_uses_the_legacy_api_key_fallback(self):
        routes = _load_routes_module(modern=False)
        self.assertEqual("check_api_key", routes.check_api_key.__name__)

    def test_discovery_does_not_require_a_mediaforge_web_session(self):
        response = self.client.get(
            "/api/v1/connector/discover",
            headers={"X-Api-Key": "library:read-key"},
        )
        self.assertEqual(200, response.status_code)
        self.assertEqual("Example", response.get_json()["rows"]["new"][0]["title"])
        self.assertEqual(1, len(response.get_json()["rows"]["new"]))
        self.assertTrue(
            response.get_json()["rows"]["new"][0]["poster_url"].startswith(
                "/api/img?url="
            )
        )

    def test_sources_always_filter_adult_content_for_api_key_requests(self):
        headers = {"X-Api-Key": "library:read-key"}
        response = self.client.get("/api/v1/connector/sources", headers=headers)
        self.assertEqual(200, response.status_code)
        self.assertEqual(["aniworld"], [item["id"] for item in response.get_json()["sources"]])
        self.assertEqual(["aniworld"], response.get_json()["order"])

    def test_search_rejects_a_disabled_source(self):
        response = self.client.post(
            "/api/v1/connector/search",
            json={"keyword": "Example", "site": "disabled"},
            headers={"X-Api-Key": "library:read-key"},
        )
        self.assertEqual(400, response.status_code)
        self.assertEqual("source disabled", response.get_json()["error"])
        self.assertEqual(["api_search_sources"], self.calls)

    def test_sources_reject_client_controlled_adult_filter(self):
        response = self.client.get(
            "/api/v1/connector/sources?include_adult=true",
            headers={"X-Api-Key": "library:read-key"},
        )
        self.assertEqual(400, response.status_code)

    def test_search_blocks_adult_source_and_rejects_override(self):
        headers = {"X-Api-Key": "library:read-key"}
        response = self.client.post(
            "/api/v1/connector/search",
            json={"keyword": "Example", "site": "hanime"},
            headers=headers,
        )
        self.assertEqual(403, response.status_code)
        self.assertEqual(["api_search_sources"], self.calls)

        self.calls.clear()
        response = self.client.post(
            "/api/v1/connector/search",
            json={"keyword": "Example", "site": "hanime", "include_adult": True},
            headers=headers,
        )
        self.assertEqual(400, response.status_code)
        self.assertEqual([], self.calls)

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
        self.assertTrue(
            response.get_json()["poster_url"].startswith(
                "/api/img?url="
            )
        )

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

    def test_poster_rewrite_parses_existing_proxy_queries_strictly(self):
        routes = _load_routes_module()
        valid = {
            "poster_url": "/api/img?url=https%3A%2F%2Fallowed.invalid%2Fposter.jpg"
        }
        routes._proxy_poster(valid)
        self.assertEqual(
            "/api/img?url=https%3A%2F%2Fallowed.invalid%2Fposter.jpg",
            valid["poster_url"],
        )

        for malformed in (
            "/api/img?url=https%3A%2F%2Fallowed.invalid%2Fposter.jpg&foo=bar",
            "/api/img?url=https%3A%2F%2Fa.invalid%2Fx&url=https%3A%2F%2Fb.invalid%2Fx",
            "/api/img?url=file%3A%2F%2F%2Fetc%2Fpasswd",
        ):
            payload = {"poster_url": malformed}
            routes._proxy_poster(payload)
            self.assertEqual("", payload["poster_url"])

    def test_recursive_poster_rewrite_does_not_change_unrelated_objects(self):
        routes = _load_routes_module()
        payload = {"rows": {"new": [{"title": "No poster"}]}}
        routes._proxy_posters(payload)
        self.assertEqual({"rows": {"new": [{"title": "No poster"}]}}, payload)

    def test_episode_download_state_is_passed_through_without_provider_io(self):
        response = self.client.get(
            "/api/v1/connector/episodes?url=https://allowed.invalid/media/movie",
            headers={"X-Api-Key": "library:read-key"},
        )
        self.assertEqual(200, response.status_code)
        self.assertFalse(response.get_json()["episodes"][0]["downloaded"])
        self.assertEqual(["api_episodes"], self.calls)

    def test_poster_rewrite_rejects_oversized_absolute_url(self):
        routes = _load_routes_module()
        payload = {
            "poster_url": "https://allowed.invalid/" + "a" * routes._MAX_URL_LENGTH
        }
        routes._proxy_poster(payload)
        self.assertEqual("", payload["poster_url"])

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
            json={**base, "custom_path_id": 999},
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
                "episodes": [
                    "https://allowed.invalid/media/episode-1",
                    "https://allowed.invalid/media/episode-2",
                ],
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
        self.assertEqual(42, response.get_json()["queue_id"])
        self.assertEqual(2, response.get_json()["accepted_episode_count"])
        self.assertEqual(11, self.download_bodies[0]["custom_path_id"])
        self.assertNotIn("/private/series", response.get_data(as_text=True))

    def test_movie_and_series_defaults_follow_mediaforge_site_assignments(self):
        headers = {"X-Api-Key": "queue:write-key"}
        base = {
            "episodes": ["https://allowed.invalid/media/episode-1"],
            "language": "German Dub",
            "provider": "VOE",
            "title": "Title",
            "upscale": False,
        }
        series_response = self.client.post(
            "/api/v1/connector/download",
            json={**base, "series_url": "https://allowed.invalid/media/series"},
            headers=headers,
        )
        movie_response = self.client.post(
            "/api/v1/connector/download",
            json={**base, "series_url": "https://allowed.invalid/media/movie"},
            headers=headers,
        )

        self.assertEqual(200, series_response.status_code)
        self.assertEqual(200, movie_response.status_code)
        self.assertEqual(11, self.download_bodies[0]["custom_path_id"])
        self.assertEqual(12, self.download_bodies[1]["custom_path_id"])

    def test_no_site_default_preserves_mediaforge_global_download_path(self):
        self.routes.site_for_url = lambda _url: "megakino"
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
        self.assertNotIn("custom_path_id", self.download_bodies[0])

    def test_duplicate_site_defaults_use_mediaforge_database_order(self):
        self.routes.get_custom_paths = lambda: [
            {"id": 21, "default_sites": "aniworld"},
            {"id": 22, "default_sites": "aniworld"},
        ]
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
        self.assertEqual(21, self.download_bodies[0]["custom_path_id"])

    def test_invalid_or_unavailable_path_configuration_fails_before_queueing(self):
        headers = {"X-Api-Key": "queue:write-key"}
        body = {
            "episodes": ["https://allowed.invalid/media/episode-1"],
            "language": "German Dub",
            "provider": "VOE",
            "title": "Title",
            "series_url": "https://allowed.invalid/media/series",
            "upscale": False,
        }
        for loader in (
            lambda: [{"id": "not-an-integer", "default_sites": "aniworld"}],
            lambda: (_ for _ in ()).throw(RuntimeError("database unavailable")),
        ):
            with self.subTest(loader=loader):
                self.calls.clear()
                self.download_bodies.clear()
                self.routes.get_custom_paths = loader
                response = self.client.post(
                    "/api/v1/connector/download",
                    json=body,
                    headers=headers,
                )
                self.assertEqual(503, response.status_code)
                self.assertEqual([], self.calls)
                self.assertEqual([], self.download_bodies)
                self.assertNotIn("/private/", response.get_data(as_text=True))

    def test_optional_queue_count_check_cannot_turn_success_into_failure(self):
        def unavailable(_queue_id):
            raise RuntimeError("database temporarily unavailable")

        self.routes.get_queue_item = unavailable
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
        self.assertEqual({"queue_id": 42}, response.get_json())

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
        module, registrations, scope_registrations, _legacy_scopes = _load_connector_package()

        class FakeApp:
            def __init__(self):
                self.blueprints = []

            def register_blueprint(self, blueprint):
                self.blueprints.append(blueprint)

        app = FakeApp()
        module.register(app)

        self.assertEqual("1.5.0", module.MODULE_MIN_APP_VERSION)
        self.assertEqual("1.6.999", module.MODULE_MAX_APP_VERSION)
        self.assertEqual(1, len(app.blueprints))
        self.assertEqual(1, len(registrations))
        self.assertEqual("mediaforge_jellyfin_connector", registrations[0]["item_id"])
        self.assertEqual("settings", registrations[0]["settings_host"])
        self.assertEqual("mediaforge_jellyfin_connector", registrations[0]["blueprint"])
        self.assertEqual("module:mediaforge_jellyfin_connector:enabled", registrations[0]["enabled_setting_key"])
        self.assertEqual(1, len(scope_registrations))
        self.assertEqual("mediaforge_jellyfin_connector", scope_registrations[0][0])
        self.assertEqual(
            {"mediaforge_jellyfin_connector.connector_health": "status:read"},
            scope_registrations[0][1],
        )
        self.assertEqual(
            {"blueprint": "mediaforge_jellyfin_connector"},
            scope_registrations[0][2],
        )

    def test_mediaforge_15_uses_the_legacy_scope_registry(self):
        module, registrations, scope_registrations, legacy_scopes = _load_connector_package(
            modern=False
        )

        class FakeApp:
            def register_blueprint(self, _blueprint):
                pass

        module.register(FakeApp())
        self.assertEqual([], scope_registrations)
        self.assertEqual(
            {"mediaforge_jellyfin_connector.connector_health": "status:read"},
            legacy_scopes,
        )
        self.assertNotIn("blueprint", registrations[0])


if __name__ == "__main__":
    unittest.main()
