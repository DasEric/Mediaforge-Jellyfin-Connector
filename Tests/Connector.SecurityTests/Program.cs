using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Jellyfin.Plugin.MediaForge;
using Jellyfin.Plugin.MediaForge.Configuration;
using Jellyfin.Plugin.MediaForge.Api;
using Jellyfin.Plugin.MediaForge.Helpers;
using Jellyfin.Plugin.MediaForge.Models;
using Jellyfin.Plugin.MediaForge.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

var testRoot = Path.Combine(Path.GetTempPath(), "mediaforge-connector-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    TestSecretStore(testRoot);
    TestConfigurationSerialization();
    TestAuthorizationBoundaries();
    TestApiJsonContracts();
    TestPosterProxyContract();
    TestJellyfinLibraryMatching();
    TestJellyfinLibraryQueries();
    TestServiceRegistrationAndImageTypes();
    TestQueueResponseContract();
    TestPluginPageRegistration();
    TestRequestPageContract();
    TestWebInjection();
    TestMediaGrants();
    TestRateLimiter();
    await TestRequestStoreAsync(testRoot);
    Console.WriteLine("All connector security tests passed.");
    return 0;
}
finally
{
    Directory.Delete(testRoot, recursive: true);
}

static void TestConfigurationSerialization()
{
    const string token = "mf_test_must_not_be_serialized";
    var json = JsonSerializer.Serialize(new PluginConfiguration { MediaForgeApiKey = token });
    Assert(!json.Contains(token, StringComparison.Ordinal), "Legacy API key was exposed through JSON configuration.");
    Assert(!json.Contains("MediaForgeApiKey", StringComparison.Ordinal), "Legacy API key property was exposed through JSON configuration.");
    var newtonsoftJson = Newtonsoft.Json.JsonConvert.SerializeObject(new PluginConfiguration { MediaForgeApiKey = token });
    Assert(!newtonsoftJson.Contains(token, StringComparison.Ordinal), "Legacy API key was exposed through Newtonsoft JSON configuration.");
    Assert(!newtonsoftJson.Contains("MediaForgeApiKey", StringComparison.Ordinal), "Legacy API key property was exposed through Newtonsoft JSON configuration.");
}

static void TestWebInjection()
{
    const string index = "<html><body><main>Jellyfin</main></body></html>";
    var enabled = TransformationPatches.ApplyIndexHtml(new PatchRequestPayload { Contents = index }, enabled: true);
    Assert(enabled.Contains("MediaForgeRequests/InjectionScript", StringComparison.Ordinal), "User navigation script was not injected.");
    Assert(enabled.IndexOf("MediaForgeRequests/InjectionScript", StringComparison.Ordinal)
        == enabled.LastIndexOf("MediaForgeRequests/InjectionScript", StringComparison.Ordinal), "User navigation script was injected more than once.");

    var enabledAgain = TransformationPatches.ApplyIndexHtml(new PatchRequestPayload { Contents = enabled }, enabled: true);
    Assert(enabledAgain == enabled, "Repeated user navigation injection was not idempotent.");

    var disabled = TransformationPatches.ApplyIndexHtml(new PatchRequestPayload { Contents = enabled }, enabled: false);
    Assert(!disabled.Contains("MediaForgeRequests/InjectionScript", StringComparison.Ordinal), "Disabled user navigation script was not removed.");
}

static void TestPluginPageRegistration()
{
    var pages = Plugin.CreatePages();
    var menuPages = pages.Where(page => page.EnableInMainMenu).ToArray();
    Assert(menuPages.Length == 1, "Exactly one plugin page must be exposed in the administrator menu.");
    Assert(menuPages[0].Name == "MediaForgeRequestsConfig", "Jellyfin does not open the connector settings page by default.");
    Assert(pages[0].Name == "MediaForgeRequestsConfig", "The settings page must be Jellyfin's first configuration-page candidate.");

    var assembly = typeof(Plugin).Assembly;
    using var stream = assembly.GetManifestResourceStream("Jellyfin.Plugin.MediaForge.Web.config.html")
        ?? throw new InvalidOperationException("Embedded settings page is missing.");
    using var reader = new StreamReader(stream, Encoding.UTF8);
    var html = reader.ReadToEnd();
    Assert(
        html.Contains("data-controller=\"__plugin/MediaForgeRequestsConfigJS\"", StringComparison.Ordinal),
        "The settings page does not load its controller script.");
    Assert(html.Contains("id=\"mfApiKey\"", StringComparison.Ordinal), "The MediaForge API-key input is missing from settings.");
}

static void TestRequestPageContract()
{
    var assembly = typeof(Plugin).Assembly;
    using var scriptStream = assembly.GetManifestResourceStream("Jellyfin.Plugin.MediaForge.Web.requests.js")
        ?? throw new InvalidOperationException("Embedded requests script is missing.");
    using var scriptReader = new StreamReader(scriptStream, Encoding.UTF8);
    var script = scriptReader.ReadToEnd();
    Assert(script.Contains("call('Discover')", StringComparison.Ordinal), "The Requests page does not load MediaForge discovery rows.");
    Assert(script.Contains("call('Requests/Automatic'", StringComparison.Ordinal), "The Requests page does not use server-calculated missing-media requests.");
    Assert(script.Contains("q('discover').hidden = searching", StringComparison.Ordinal), "Search results do not hide the discovery feed.");
    Assert(script.Contains("URL.createObjectURL", StringComparison.Ordinal), "Poster images are not loaded through authenticated blobs.");
    Assert(script.Contains("searchGeneration", StringComparison.Ordinal), "Stale searches can overwrite newer results.");
    Assert(script.Contains("detailGeneration", StringComparison.Ordinal), "Stale detail requests can overwrite the active dialog.");
    Assert(script.Contains("if (generation === detailGeneration) q('request').disabled = false", StringComparison.Ordinal), "An older request can mutate a newer dialog.");
    Assert(script.Contains("response.clone().json()", StringComparison.Ordinal), "Structured API errors are not shown to users.");
    Assert(script.Contains("available: 'Bereits in Jellyfin vorhanden'", StringComparison.Ordinal), "Approval-time availability is not represented in the UI.");
    Assert(script.Contains("items.some((item) => item.status === 'queued')", StringComparison.Ordinal), "A temporary progress error permanently stops polling queued downloads.");
    Assert(!script.Contains("accessToken()", StringComparison.Ordinal), "The Requests page must not embed the Jellyfin token in image URLs.");
    Assert(!script.Contains("api_key", StringComparison.OrdinalIgnoreCase), "The Requests page must not put API keys in URLs.");

    using var htmlStream = assembly.GetManifestResourceStream("Jellyfin.Plugin.MediaForge.Web.requests.html")
        ?? throw new InvalidOperationException("Embedded requests page is missing.");
    using var htmlReader = new StreamReader(htmlStream, Encoding.UTF8);
    var html = htmlReader.ReadToEnd();
    Assert(html.Contains("data-mf=\"discover\"", StringComparison.Ordinal), "The Requests page has no discovery container.");
}

static void TestAuthorizationBoundaries()
{
    var controller = typeof(MediaForgeRequestsController);
    Assert(
        controller.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Length > 0,
        "The requests controller must require an authenticated Jellyfin user.");

    var userMethods = new[]
    {
        nameof(MediaForgeRequestsController.GetStatus),
        nameof(MediaForgeRequestsController.GetSources),
        nameof(MediaForgeRequestsController.Discover),
        nameof(MediaForgeRequestsController.Image),
        nameof(MediaForgeRequestsController.Search),
        nameof(MediaForgeRequestsController.GetSeries),
        nameof(MediaForgeRequestsController.GetSeasons),
        nameof(MediaForgeRequestsController.GetEpisodes),
        nameof(MediaForgeRequestsController.GetProviders),
        nameof(MediaForgeRequestsController.PlanRequest),
        nameof(MediaForgeRequestsController.CreateAutomaticRequest),
        nameof(MediaForgeRequestsController.GetMyRequests),
        nameof(MediaForgeRequestsController.GetMyProgress),
        nameof(MediaForgeRequestsController.WithdrawRequest),
        nameof(MediaForgeRequestsController.CreateRequest),
    };
    foreach (var methodName in userMethods)
    {
        var method = controller.GetMethod(methodName) ?? throw new InvalidOperationException($"Missing user endpoint {methodName}.");
        var policies = method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy);
        Assert(!policies.Contains(Policies.RequiresElevation), $"User endpoint {methodName} unexpectedly requires administrator elevation.");
    }

    var adminMethods = new[]
    {
        nameof(MediaForgeRequestsController.GetAllRequests),
        nameof(MediaForgeRequestsController.Approve),
        nameof(MediaForgeRequestsController.Reject),
        nameof(MediaForgeRequestsController.GetApiKeyStatus),
        nameof(MediaForgeRequestsController.UpdateApiKey),
        nameof(MediaForgeRequestsController.DeleteApiKey),
        nameof(MediaForgeRequestsController.TestConnection),
    };
    foreach (var methodName in adminMethods)
    {
        var method = controller.GetMethod(methodName) ?? throw new InvalidOperationException($"Missing admin endpoint {methodName}.");
        var policies = method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy);
        Assert(policies.Contains(Policies.RequiresElevation), $"Admin endpoint {methodName} is missing the elevation policy.");
    }
}

static void TestApiJsonContracts()
{
    var requestJson = JsonSerializer.Serialize(new MediaRequest
    {
        Id = 7,
        Username = "User",
        Title = "Title",
        SelectionLabel = "1 fehlende Episode",
        EpisodesJson = "[\"https://example.invalid/episode/1\"]",
        Language = "German Dub",
        Status = RequestStatuses.Pending,
        CreatedUtc = DateTime.UnixEpoch,
    });
    using (var requestDocument = JsonDocument.Parse(requestJson))
    {
        var root = requestDocument.RootElement;
        foreach (var name in new[] { "id", "username", "title", "selectionLabel", "episodes", "language", "status", "createdUtc" })
        {
            Assert(root.TryGetProperty(name, out _), $"MediaRequest is missing the explicit JSON field {name}.");
        }
        Assert(!root.TryGetProperty("Title", out _), "MediaRequest leaked an unexpected PascalCase response field.");
    }

    AssertJsonNames("SourceInfo", new Dictionary<string, string>
    {
        ["Id"] = "id",
        ["Label"] = "label",
        ["Adult"] = "adult",
    });
    AssertJsonNames("SearchGroup", new Dictionary<string, string>
    {
        ["Source"] = "source",
        ["Label"] = "label",
        ["Data"] = "data",
        ["Error"] = "error",
    });

    var controllerSource = File.ReadAllText(
        Path.Combine("Jellyfin.Plugin.MediaForge", "Api", "MediaForgeRequestsController.cs"));
    Assert(
        controllerSource.Contains("sources = sources.Take(maximum).ToList()", StringComparison.Ordinal),
        "The all-source fan-out limit is not applied at search time.");
    Assert(
        controllerSource.Contains("output.Count >= MaxKnownSources", StringComparison.Ordinal),
        "The source catalogue has no independent safety bound.");
    Assert(
        controllerSource.Contains("refreshAvailability: true", StringComparison.Ordinal)
        && controllerSource.Contains("requireGrant: false", StringComparison.Ordinal),
        "Administrator approval does not refresh Jellyfin library availability.");

    var readSources = typeof(MediaForgeRequestsController).GetMethod(
        "ReadAllowedSources",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing source response validator.");
    using var malformedSources = JsonDocument.Parse(
        """{"sources":[null,"bad",{"id":"adult-unknown","label":"Bad","adult":"false"},{"id":"ok","label":"Okay","adult":false},{"id":"OK","label":"Duplicate","adult":false}]}""");
    var filteredSources = readSources.Invoke(null, [malformedSources.RootElement]);
    using var filteredDocument = JsonDocument.Parse(JsonSerializer.Serialize(filteredSources));
    var filteredArray = filteredDocument.RootElement;
    Assert(filteredArray.GetArrayLength() == 1, "Malformed or duplicate MediaForge sources were not rejected.");
    Assert(filteredArray[0].GetProperty("id").GetString() == "ok", "The valid source was lost during filtering.");
}

static void TestPosterProxyContract()
{
    var method = typeof(MediaForgeRequestsController).GetMethod(
        "TryReadMediaForgeImageUrl",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing poster URL validator.");
    object?[] coreArgs =
    [
        "/api/img?url=https%3A%2F%2Fimages.example.invalid%2Fposter.jpg",
        null,
    ];
    Assert((bool)method.Invoke(null, coreArgs)!, "The MediaForge image route was rejected.");
    Assert(
        coreArgs[1] as string == "https://images.example.invalid/poster.jpg",
        "The MediaForge image route did not decode to the expected upstream URL.");

    object?[] connectorArgs =
    [
        "/api/v1/connector/image?url=https%3A%2F%2Fimages.example.invalid%2Fposter.jpg",
        null,
    ];
    Assert(!(bool)method.Invoke(null, connectorArgs)!, "The compatibility connector route leaked into browser-facing URLs.");

    object?[] injectedArgs =
    [
        "/api/img?url=https%3A%2F%2Fimages.example.invalid%2Fposter.jpg&extra=value",
        null,
    ];
    Assert(!(bool)method.Invoke(null, injectedArgs)!, "An image URL containing an extra query field was accepted.");

    var clientSource = File.ReadAllText(
        Path.Combine("Jellyfin.Plugin.MediaForge", "Services", "MediaForgeClient.cs"));
    Assert(
        !clientSource.Contains("include_adult", StringComparison.Ordinal),
        "Jellyfin still lets clients opt into MediaForge Adult sources.");
    Assert(
        clientSource.Contains("api/img?url=", StringComparison.Ordinal),
        "Jellyfin does not use MediaForge's supported image proxy.");
}

static void TestJellyfinLibraryMatching()
{
    Assert(
        JellyfinLibraryAvailabilityService.NormalizeTitle("Déjà-vu: The Show!") == "dejavutheshow",
        "Library title normalization is not stable across punctuation and diacritics.");
    Assert(
        JellyfinLibraryAvailabilityService.ProviderIdsMatch(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt123" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["IMDB"] = "TT123" }),
        "Provider IDs did not match case-insensitively.");

    var episodes = JellyfinLibraryAvailabilityService.BuildEpisodeSet(
    [
        new Episode { ParentIndexNumber = 0, IndexNumber = 1 },
        new Episode { ParentIndexNumber = 2, IndexNumber = 3, IndexNumberEnd = 5 },
        new Episode { ParentIndexNumber = null, IndexNumber = 9 },
    ]);
    Assert(episodes.Contains(new LibraryEpisodeKey(0, 1)), "Specials were not included in library availability.");
    Assert(episodes.Contains(new LibraryEpisodeKey(2, 3)), "A normal Jellyfin episode was not included.");
    Assert(episodes.Contains(new LibraryEpisodeKey(2, 4)), "A multi-episode file did not include its middle episode.");
    Assert(episodes.Contains(new LibraryEpisodeKey(2, 5)), "A multi-episode file did not include its final episode.");
    Assert(!episodes.Contains(new LibraryEpisodeKey(2, 6)), "A multi-episode file included an unrelated episode.");
}

static void TestJellyfinLibraryQueries()
{
    var movie = new Movie
    {
        Id = Guid.NewGuid(),
        Name = "Dune",
        ProductionYear = 2021,
    };
    movie.ProviderIds["Imdb"] = "tt1160419";

    var series = new Series
    {
        Id = Guid.NewGuid(),
        Name = "Example Show",
        ProductionYear = 2024,
    };
    series.ProviderIds["Tvdb"] = "12345";
    var episodes = new BaseItem[]
    {
        new Episode { ParentIndexNumber = 1, IndexNumber = 1 },
        new Episode { ParentIndexNumber = 1, IndexNumber = 2, IndexNumberEnd = 3 },
    };

    IReadOnlyList<BaseItem> movieResults = [movie];
    var queries = new List<InternalItemsQuery>();
    var manager = LibraryManagerProxy.Create(query =>
    {
        queries.Add(query);
        if (query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Episode))
        {
            return episodes;
        }

        if (query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Movie))
        {
            return movieResults;
        }

        if (query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Series))
        {
            return [series];
        }

        return Array.Empty<BaseItem>();
    });
    var availability = new JellyfinLibraryAvailabilityService(manager);

    var movieState = availability.GetAvailability(new LibraryMediaIdentity(
        "Different localized title",
        2021,
        true,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "TT1160419" }));
    Assert(movieState.ItemExists, "A Jellyfin movie with the same provider ID was not detected.");

    var titleFallback = availability.GetAvailability(new LibraryMediaIdentity(
        "Dune",
        2021,
        true,
        new Dictionary<string, string>()));
    Assert(titleFallback.ItemExists, "The conservative Jellyfin title/year fallback did not find a movie.");

    var conflictingId = availability.GetAvailability(new LibraryMediaIdentity(
        "Dune",
        2021,
        true,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt9999999" }));
    Assert(!conflictingId.ItemExists, "A conflicting Jellyfin provider ID was ignored during title fallback.");

    movieResults =
    [
        movie,
        new Movie { Id = Guid.NewGuid(), Name = "Dune", ProductionYear = 1984 },
    ];
    var ambiguousTitle = availability.GetAvailability(new LibraryMediaIdentity(
        "Dune",
        null,
        true,
        new Dictionary<string, string>()));
    Assert(!ambiguousTitle.ItemExists, "An ambiguous title without year or provider ID suppressed a download.");

    var seriesState = availability.GetAvailability(new LibraryMediaIdentity(
        "Example Show",
        2024,
        false,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tvdb"] = "12345" }));
    Assert(seriesState.ItemExists, "A Jellyfin series with the same provider ID was not detected.");
    Assert(seriesState.Episodes.SetEquals([
        new LibraryEpisodeKey(1, 1),
        new LibraryEpisodeKey(1, 2),
        new LibraryEpisodeKey(1, 3),
    ]), "The Jellyfin episode query did not produce the expected availability set.");

    var episodeQuery = queries.Single(query => query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Episode));
    Assert(episodeQuery.Recursive, "Jellyfin episodes are not queried recursively.");
    Assert(episodeQuery.IsVirtualItem == false, "Virtual Jellyfin episodes must not count as downloaded files.");
    Assert(episodeQuery.AncestorIds.SequenceEqual([series.Id]), "Episode availability is not restricted to the matched Jellyfin series.");
}

static void TestServiceRegistrationAndImageTypes()
{
    var services = new ServiceCollection();
    new PluginServiceRegistrator().RegisterServices(services, null!);
    Assert(
        services.Any(descriptor => descriptor.ServiceType == typeof(JellyfinLibraryAvailabilityService)),
        "The Jellyfin library availability service is missing from dependency injection.");

    var field = typeof(MediaForgeClient).GetField(
        "AllowedImageTypes",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing image media-type allowlist.");
    var allowed = (IReadOnlySet<string>)field.GetValue(null)!;
    Assert(allowed.Contains("image/jpeg") && allowed.Contains("image/png"), "Safe poster types are missing.");
    Assert(!allowed.Contains("image/svg+xml"), "Active SVG content must not be served from the Jellyfin origin.");
}

static void TestQueueResponseContract()
{
    var method = typeof(MediaForgeClient).GetMethod(nameof(MediaForgeClient.QueueAsync))
        ?? throw new InvalidOperationException("Missing MediaForge queue method.");
    Assert(method.ReturnType == typeof(Task<long>), "A queue submission may still succeed without a trackable queue ID.");

    var source = File.ReadAllText(Path.Combine(
        "Jellyfin.Plugin.MediaForge",
        "Services",
        "MediaForgeClient.cs"));
    Assert(
        source.Contains("numeric > 0", StringComparison.Ordinal),
        "MediaForge queue IDs are not validated as positive values.");
    Assert(
        source.Contains("keine gültige Warteschlangen-ID", StringComparison.Ordinal),
        "Malformed successful queue responses do not fail closed.");
}

static void AssertJsonNames(string nestedTypeName, IReadOnlyDictionary<string, string> expected)
{
    var type = typeof(MediaForgeRequestsController).GetNestedType(
        nestedTypeName,
        System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing API response type {nestedTypeName}.");
    foreach (var pair in expected)
    {
        var property = type.GetProperty(pair.Key)
            ?? throw new InvalidOperationException($"Missing {nestedTypeName}.{pair.Key}.");
        var attribute = property.GetCustomAttributes(typeof(JsonPropertyNameAttribute), inherit: true)
            .Cast<JsonPropertyNameAttribute>()
            .SingleOrDefault();
        Assert(attribute?.Name == pair.Value, $"{nestedTypeName}.{pair.Key} must serialize as {pair.Value}.");
    }
}

static void TestSecretStore(string testRoot)
{
    const string token = "mf_test_super_secret_token_123456";
    var store = new SecretStore(testRoot);
    Assert(!store.HasApiKey, "A fresh secret store must be empty.");
    store.SetApiKey(token);
    Assert(store.HasApiKey, "Stored API key was not detected.");
    Assert(store.GetApiKey() == token, "Stored API key could not be decrypted.");

    foreach (var file in Directory.EnumerateFiles(testRoot))
    {
        var contents = File.ReadAllBytes(file);
        Assert(!Encoding.UTF8.GetString(contents).Contains(token, StringComparison.Ordinal), "API key was stored in plaintext.");
    }

    var secretPath = Path.Combine(testRoot, "mediaforge-api-key.bin");
    var tampered = File.ReadAllBytes(secretPath);
    tampered[^1] ^= 0x5A;
    File.WriteAllBytes(secretPath, tampered);
    Assert(store.GetApiKey() is null, "Tampered ciphertext must fail closed.");

    store.SetApiKey(token);
    store.ClearApiKey();
    Assert(!store.HasApiKey, "Cleared API key remained available.");
}

static void TestMediaGrants()
{
    var grants = new MediaAccessGrantStore();
    using var document = JsonDocument.Parse("""
        {
          "results": [
            { "title": "Allowed", "url": "https://example.invalid/series/allowed", "poster_url": "https://images.invalid/poster.jpg" }
          ]
        }
        """);
    grants.GrantFromJson("user-a", "source-a", document.RootElement);

    Assert(grants.IsGranted("user-a", "https://example.invalid/series/allowed", out var source), "Returned media URL was not granted.");
    Assert(source == "source-a", "Granted URL has the wrong source.");
    Assert(!grants.IsGranted("user-b", "https://example.invalid/series/allowed", out _), "A grant leaked to another user.");
    Assert(!grants.IsGranted("user-a", "https://example.invalid/series/injected", out _), "An arbitrary URL was accepted.");
    Assert(!grants.IsGranted("user-a", "https://images.invalid/poster.jpg", out _), "Poster URL was incorrectly granted as media.");
    Assert(!grants.IsGranted("user-a", "file:///etc/passwd", out _), "A non-HTTP URL was accepted.");
}

static void TestRateLimiter()
{
    var limiter = new UserRateLimiter();
    Assert(limiter.TryConsume("user-a", "search", 2, TimeSpan.FromMinutes(1)), "First request was rejected.");
    Assert(limiter.TryConsume("user-a", "search", 2, TimeSpan.FromMinutes(1)), "Second request was rejected.");
    Assert(!limiter.TryConsume("user-a", "search", 2, TimeSpan.FromMinutes(1)), "Rate limit was not enforced.");
    Assert(limiter.TryConsume("user-b", "search", 2, TimeSpan.FromMinutes(1)), "Rate limit leaked between users.");
}

static async Task TestRequestStoreAsync(string testRoot)
{
    var storePath = Path.Combine(testRoot, "requests");
    var store = new RequestStore(storePath);
    var request = new CreateMediaRequest
    {
        Title = "Test",
        SeriesUrl = "https://example.invalid/series/test",
        Source = "source-a",
        Episodes = ["https://example.invalid/episode/1"],
        Language = "German Dub",
        Provider = "VOE",
    };
    var attempts = Enumerable.Range(0, 20)
        .Select(_ => store.TryAddAsync("user-a", "User", request, RequestStatuses.Pending, 10, CancellationToken.None));
    var results = await Task.WhenAll(attempts);
    Assert(results.Count(result => result.Request is not null) == 1, "Concurrent duplicate requests were inserted.");
    Assert(results.Count(result => result.Duplicate is not null) == 19, "Concurrent duplicates were not reported consistently.");
    Assert((await store.ListAllAsync(100, CancellationToken.None)).Count == 1, "Request store contains duplicate records.");

    var firstId = results.Single(result => result.Request is not null).Request!.Id;
    Assert(
        await store.TryWithdrawAsync(firstId, "user-b", "Other", CancellationToken.None) == WithdrawRequestResult.NotFound,
        "A different user could address another user's request.");
    Assert(
        await store.TryWithdrawAsync(firstId, "user-a", "User", CancellationToken.None) == WithdrawRequestResult.Withdrawn,
        "The owner could not withdraw a pending request.");
    Assert((await store.GetAsync(firstId, CancellationToken.None))?.Status == RequestStatuses.Withdrawn, "Withdrawn status was not persisted.");

    request.Episodes = ["https://example.invalid/episode/2"];
    var second = await store.TryAddAsync("user-a", "User", request, RequestStatuses.Pending, 10, CancellationToken.None);
    var claimTask = store.TryClaimAsync(second.Request!.Id, CancellationToken.None);
    var withdrawTask = store.TryWithdrawAsync(second.Request.Id, "user-a", "User", CancellationToken.None);
    await Task.WhenAll(claimTask, withdrawTask);
    Assert(
        claimTask.Result != (withdrawTask.Result == WithdrawRequestResult.Withdrawn),
        "Approval and withdrawal race did not have exactly one winner.");

    request.Episodes = ["https://example.invalid/episode/3"];
    var third = await store.TryAddAsync("user-a", "User", request, RequestStatuses.Pending, 10, CancellationToken.None);
    Assert(await store.TryClaimAsync(third.Request!.Id, CancellationToken.None), "Pending request could not be claimed.");
    await store.MarkQueuedAsync(third.Request.Id, 42, "Admin", CancellationToken.None);
    var otherUser = await store.TryAddAsync("user-b", "Other", request, RequestStatuses.Pending, 10, CancellationToken.None);
    Assert(await store.TryClaimAsync(otherUser.Request!.Id, CancellationToken.None), "Other user's request could not be claimed.");
    await store.MarkQueuedAsync(otherUser.Request.Id, 42, "Admin", CancellationToken.None);
    var blockedDuplicate = await store.TryAddAsync("user-a", "User", request, RequestStatuses.Pending, 10, CancellationToken.None);
    Assert(blockedDuplicate.Duplicate is not null, "A queued request did not block an immediate duplicate download.");
    await store.SyncQueueStatesAsync(
        "user-a",
        new Dictionary<long, string> { [42] = RequestStatuses.Completed },
        CancellationToken.None);
    Assert(
        (await store.GetAsync(third.Request.Id, CancellationToken.None))?.Status == RequestStatuses.Completed,
        "A terminal MediaForge queue status was not persisted.");
    Assert(
        (await store.GetAsync(otherUser.Request.Id, CancellationToken.None))?.Status == RequestStatuses.Queued,
        "Progress synchronization changed another user's request.");
    var afterCompletion = await store.TryAddAsync("user-a", "User", request, RequestStatuses.Pending, 10, CancellationToken.None);
    var approvalRequest = afterCompletion.Request
        ?? throw new InvalidOperationException("A completed download continued to block a future request.");
    Assert(await store.TryClaimAsync(approvalRequest.Id, CancellationToken.None), "Request could not be claimed for approval-time refresh.");
    Assert(
        await store.TryUpdateProcessingPlanAsync(
            approvalRequest.Id,
            "Updated title",
            "series",
            "1 fehlende Episode",
            ["https://example.invalid/episode/4"],
            CancellationToken.None),
        "Approval-time missing-media plan could not be persisted.");
    var refreshed = await store.GetAsync(approvalRequest.Id, CancellationToken.None);
    Assert(refreshed?.Title == "Updated title" && refreshed.Episodes.Single().EndsWith("/4", StringComparison.Ordinal), "Refreshed missing-media selection was not stored.");
    await store.MarkAvailableAsync(approvalRequest.Id, "Admin", CancellationToken.None);
    Assert(
        (await store.GetAsync(approvalRequest.Id, CancellationToken.None))?.Status == RequestStatuses.Available,
        "Already-available approval status was not persisted.");

    var capacityPath = Path.Combine(testRoot, "request-capacity");
    var boundedStore = new RequestStore(capacityPath, maxStoredRequests: 2, maxStoreBytes: 1024 * 1024);
    request.Episodes = ["https://example.invalid/episode/capacity-1"];
    var oldTerminal = await boundedStore.TryAddAsync("user-a", "User", request, RequestStatuses.Pending, 10, CancellationToken.None);
    var oldTerminalId = oldTerminal.Request?.Id
        ?? throw new InvalidOperationException("Bounded request store rejected its first request.");
    Assert(
        await boundedStore.TryWithdrawAsync(oldTerminalId, "user-a", "User", CancellationToken.None)
            == WithdrawRequestResult.Withdrawn,
        "Bounded request store test could not create a terminal request.");

    request.Episodes = ["https://example.invalid/episode/capacity-2"];
    Assert(
        (await boundedStore.TryAddAsync("user-b", "User", request, RequestStatuses.Pending, 10, CancellationToken.None)).Request is not null,
        "Bounded request store rejected a request below capacity.");
    request.Episodes = ["https://example.invalid/episode/capacity-3"];
    Assert(
        (await boundedStore.TryAddAsync("user-c", "User", request, RequestStatuses.Pending, 10, CancellationToken.None)).Request is not null,
        "Bounded request store did not prune its oldest terminal request.");
    request.Episodes = ["https://example.invalid/episode/capacity-4"];
    var capacityResult = await boundedStore.TryAddAsync("user-d", "User", request, RequestStatuses.Pending, 10, CancellationToken.None);
    Assert(capacityResult.StoreCapacityReached, "Bounded request store exceeded its hard record limit.");
    Assert(capacityResult.Request is null, "A request was returned after the store reached capacity.");
    Assert((await boundedStore.ListAllAsync(10, CancellationToken.None)).Count == 2, "Bounded request store persisted too many records.");

    var recoveryPath = Path.Combine(testRoot, "request-recovery");
    var preRestartStore = new RequestStore(recoveryPath);
    request.Episodes = ["https://example.invalid/episode/recovery"];
    var interrupted = await preRestartStore.TryAddAsync("user-a", "User", request, RequestStatuses.Pending, 10, CancellationToken.None);
    var interruptedId = interrupted.Request?.Id
        ?? throw new InvalidOperationException("Restart recovery test could not add its request.");
    Assert(
        await preRestartStore.TryClaimAsync(interruptedId, CancellationToken.None),
        "Restart recovery test could not move its request to processing.");
    var postRestartStore = new RequestStore(recoveryPath);
    var recovered = await postRestartStore.GetAsync(interruptedId, CancellationToken.None);
    Assert(recovered?.Status == RequestStatuses.Failed, "An interrupted processing request was not recovered as retryable failure.");
    Assert(recovered?.DecidedBy == "recovery", "Restart recovery did not record its decision source.");

    var sizePath = Path.Combine(testRoot, "request-size-limit");
    var sizeBoundedStore = new RequestStore(sizePath, maxStoredRequests: 10, maxStoreBytes: 128);
    request.Episodes = ["https://example.invalid/episode/size-limit"];
    var sizeRejected = false;
    try
    {
        await sizeBoundedStore.TryAddAsync("user-a", "User", request, RequestStatuses.Pending, 10, CancellationToken.None);
    }
    catch (IOException)
    {
        sizeRejected = true;
    }

    Assert(sizeRejected, "Request store wrote a document beyond its hard byte limit.");
    Assert(!File.Exists(Path.Combine(sizePath, "requests.json")), "An oversized request document replaced the active store.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

public class LibraryManagerProxy : DispatchProxy
{
    private Func<InternalItemsQuery, IReadOnlyList<BaseItem>>? _query;

    public static ILibraryManager Create(Func<InternalItemsQuery, IReadOnlyList<BaseItem>> query)
    {
        var manager = Create<ILibraryManager, LibraryManagerProxy>();
        ((LibraryManagerProxy)(object)manager)._query = query;
        return manager;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(ILibraryManager.GetItemList)
            && args is { Length: 1 }
            && args[0] is InternalItemsQuery query
            && _query is not null)
        {
            return _query(query);
        }

        throw new NotSupportedException($"Unexpected ILibraryManager call: {targetMethod?.Name}");
    }
}
