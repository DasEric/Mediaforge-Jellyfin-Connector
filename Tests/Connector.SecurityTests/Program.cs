using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Jellyfin.Plugin.MediaForge;
using Jellyfin.Plugin.MediaForge.Configuration;
using Jellyfin.Plugin.MediaForge.Api;
using Jellyfin.Plugin.MediaForge.Helpers;
using Jellyfin.Plugin.MediaForge.Integration;
using Jellyfin.Plugin.MediaForge.Models;
using Jellyfin.Plugin.MediaForge.Services;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
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
    await TestEpisodePlanningContractAsync();
    TestServiceRegistrationAndImageTypes();
    TestQueueResponseContract();
    TestPluginPageRegistration();
    TestRequestPageContract();
    TestWebInjection();
    TestMediaGrants();
    TestJellixSelectionTokens();
    TestRateLimiter();
    await TestRequestStoreAsync(testRoot);
    await TestJellixBridgeAsync(testRoot);
    await TestSharedApplicationRulesAsync(testRoot);
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
    Assert(script.Contains("source: item.id", StringComparison.Ordinal), "All-source searches are not issued independently per MediaForge source.");
    Assert(script.Contains("Weitere Quellen werden durchsucht", StringComparison.Ordinal), "Progressive search does not communicate outstanding sources.");
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

    AssertJsonNames(typeof(MediaForgeSourceInfo), new Dictionary<string, string>
    {
        ["Id"] = "id",
        ["Label"] = "label",
        ["Adult"] = "adult",
        ["MediaTypes"] = "media_types",
    });
    AssertJsonNames(typeof(MediaForgeSearchGroup), new Dictionary<string, string>
    {
        ["Source"] = "source",
        ["Label"] = "label",
        ["Data"] = "data",
        ["Error"] = "error",
    });

    var controllerSource = File.ReadAllText(Path.Combine(
        "Jellyfin.Plugin.MediaForge",
        "Api",
        "MediaForgeRequestsController.cs"));
    var applicationSource = File.ReadAllText(Path.Combine(
        "Jellyfin.Plugin.MediaForge",
        "Services",
        "MediaForgeRequestApplicationService.cs"));
    Assert(
        applicationSource.Contains("sources = sources.Take(maximum).ToList()", StringComparison.Ordinal),
        "The all-source fan-out limit is not applied at search time.");
    Assert(
        applicationSource.Contains("output.Count >= MaxKnownSources", StringComparison.Ordinal),
        "The source catalogue has no independent safety bound.");
    Assert(
        applicationSource.Contains("refreshAvailability: true", StringComparison.Ordinal)
        && applicationSource.Contains("requireGrant: false", StringComparison.Ordinal),
        "Administrator approval does not refresh Jellyfin library availability.");

    var readSources = typeof(MediaForgeRequestApplicationService).GetMethod(
        "ReadAllowedSources",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing source response validator.");
    using var malformedSources = JsonDocument.Parse(
        """{"sources":[null,"bad",{"id":"adult-unknown","label":"Bad","adult":"false","media_types":["series"]},{"id":"ok","label":"Okay","adult":false,"enabled":true,"media_types":["series"]},{"id":"OK","label":"Duplicate","adult":false,"enabled":true,"media_types":["series"]}]}""");
    var filteredSources = readSources.Invoke(null, [malformedSources.RootElement, new PluginConfiguration()]);
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
    Assert(
        queries.Any(query => query.SearchTerm == "Dune" && query.NameContains is null),
        "Jellyfin title matching does not use its normalized SearchTerm query.");

    var conflictingId = availability.GetAvailability(new LibraryMediaIdentity(
        "Dune",
        2021,
        true,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Imdb"] = "tt9999999" }));
    Assert(!conflictingId.ItemExists, "A conflicting Jellyfin provider ID was ignored during title fallback.");

    movieResults =
    [
        new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Der Wüstenplanet",
            OriginalTitle = "Dune",
            ProductionYear = null,
        },
    ];
    var originalTitleWithoutYear = availability.GetAvailability(new LibraryMediaIdentity(
        "Dune",
        2021,
        true,
        new Dictionary<string, string>()));
    Assert(originalTitleWithoutYear.ItemExists, "A Jellyfin original title with missing year metadata was not detected.");

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
    Assert(
        services.Any(descriptor => descriptor.ServiceType == typeof(MediaForgeRequestApplicationService)
            && descriptor.Lifetime == ServiceLifetime.Singleton),
        "The shared MediaForge application service is not registered as a singleton.");
    Assert(
        services.Any(descriptor => descriptor.ServiceType == typeof(JellixSelectionTokenStore)
            && descriptor.Lifetime == ServiceLifetime.Singleton),
        "The Jellix selection-token store is not registered as a singleton.");
    Assert(
        services.Any(descriptor => descriptor.ServiceType == typeof(JellixBridge)
            && descriptor.Lifetime == ServiceLifetime.Singleton),
        "The concrete Jellix bridge is not registered as a singleton.");

    var bridgeType = typeof(Plugin).Assembly.GetType(
        "Jellyfin.Plugin.MediaForge.Integration.JellixBridge",
        throwOnError: true,
        ignoreCase: false)!;
    var invoke = bridgeType.GetMethod(nameof(JellixBridge.InvokeAsync))
        ?? throw new InvalidOperationException("The Jellix bridge method is missing.");
    Assert(
        invoke.ReturnType == typeof(Task<string>)
        && invoke.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual([
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(CancellationToken),
        ]),
        "The Jellix bridge reflection contract changed.");
    var bridgeSource = File.ReadAllText(Path.Combine(
        "Jellyfin.Plugin.MediaForge",
        "Integration",
        "JellixBridge.cs"));
    Assert(!bridgeSource.Contains("GetApiKey", StringComparison.Ordinal), "The Jellix bridge reads the MediaForge API key.");
    Assert(!bridgeSource.Contains("X-Api-Key", StringComparison.OrdinalIgnoreCase), "The Jellix bridge exposes an API header.");
    Assert(!bridgeSource.Contains("requests.json", StringComparison.OrdinalIgnoreCase), "The Jellix bridge accesses the request file directly.");
    Assert(!bridgeSource.Contains("MediaForgeRequestsController", StringComparison.Ordinal), "The Jellix bridge invokes the HTTP controller.");

    var field = typeof(MediaForgeClient).GetField(
        "AllowedImageTypes",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing image media-type allowlist.");
    var allowed = (IReadOnlySet<string>)field.GetValue(null)!;
    Assert(allowed.Contains("image/jpeg") && allowed.Contains("image/png"), "Safe poster types are missing.");
    Assert(!allowed.Contains("image/svg+xml"), "Active SVG content must not be served from the Jellyfin origin.");

    var searchTimeoutField = typeof(MediaForgeClient).GetField(
        "SearchTimeout",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing per-source search timeout.");
    Assert(
        (TimeSpan)searchTimeoutField.GetValue(null)! == TimeSpan.FromSeconds(15),
        "Jellyfin search does not use MediaForge's 15-second per-source deadline.");
}

static void TestQueueResponseContract()
{
    var method = typeof(MediaForgeClient).GetMethod(nameof(MediaForgeClient.QueueAsync))
        ?? throw new InvalidOperationException("Missing MediaForge queue method.");
    Assert(
        method.ReturnType == typeof(Task<MediaForgeQueueResult>),
        "A queue submission does not expose its verified queue metadata.");

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

static async Task TestEpisodePlanningContractAsync()
{
    using var validDocument = JsonDocument.Parse(
        """
        {
          "episodes": [
            {
              "url": "https://allowed.invalid/media/episode-1",
              "episode_number": 1,
              "languages": ["German Dub", "English Sub"]
            },
            {
              "url": "https://allowed.invalid/media/episode-2",
              "episode_number": "2",
              "languages": ["German Dub"]
            }
          ]
        }
        """);
    var parsed = MediaForgeEpisodeParser.Parse(validDocument.RootElement, 3);
    Assert(parsed.Count == 2, "A complete MediaForge season was not preserved.");
    Assert(parsed.All(item => item.SeasonNumber == 3), "The season fallback was not applied to every episode.");
    Assert(parsed.Select(item => item.EpisodeNumber).SequenceEqual([1, 2]), "Episode numbers changed while planning.");
    Assert(parsed[0].Languages.SetEquals(["German Dub", "English Sub"]), "Episode languages changed while planning.");
    Assert(MediaForgeEpisodeParser.SatisfiesExpectedCount(2, parsed.Count), "A complete season was treated as partial.");
    Assert(!MediaForgeEpisodeParser.SatisfiesExpectedCount(3, parsed.Count), "A partial season was accepted for queueing.");

    using var validSeason = JsonDocument.Parse("{\"episode_count\":\"2\"}");
    Assert(
        MediaForgeRequestsController.ReadExpectedEpisodeCount(validSeason.RootElement) == 2,
        "A valid expected episode count was not accepted.");
    foreach (var invalidSeasonJson in new[] { "{}", "{\"episode_count\":-1}", "{\"episode_count\":null}" })
    {
        using var invalidSeason = JsonDocument.Parse(invalidSeasonJson);
        var rejected = false;
        try
        {
            MediaForgeRequestsController.ReadExpectedEpisodeCount(invalidSeason.RootElement);
        }
        catch (Exception exception) when (exception is MediaForgeException or MediaForgeApplicationException)
        {
            rejected = true;
        }

        Assert(rejected, "An invalid expected episode count disabled completeness verification.");
    }

    foreach (var invalidJson in new[]
             {
                 "{\"episodes\":[{\"url\":\"not-a-url\"}]}",
                 "{\"episodes\":[{\"url\":\"https://allowed.invalid/media/episode-1\"},{\"url\":\"https://allowed.invalid/media/episode-1\"}]}",
                 "{\"episodes\":[{}]}",
             })
    {
        using var invalidDocument = JsonDocument.Parse(invalidJson);
        var rejected = false;
        try
        {
            MediaForgeEpisodeParser.Parse(invalidDocument.RootElement, 1);
        }
        catch (MediaForgeException)
        {
            rejected = true;
        }

        Assert(rejected, "An unsafe or incomplete episode list was silently shortened.");
    }

    using var partialDocument = JsonDocument.Parse(
        "{\"episodes\":[{\"url\":\"https://allowed.invalid/media/episode-1\",\"episode_number\":1}]}");
    var retryResponses = new Queue<JsonElement>(
        [partialDocument.RootElement.Clone(), validDocument.RootElement.Clone()]);
    var retryFetches = 0;
    var observedResponses = 0;
    var retried = await MediaForgeEpisodeParser.FetchCompleteAsync(
        _ =>
        {
            retryFetches++;
            return Task.FromResult(retryResponses.Dequeue());
        },
        _ => observedResponses++,
        3,
        2,
        CancellationToken.None);
    Assert(
        retryFetches == 2 && observedResponses == 2 && retried.Count == 2,
        "An incomplete first season response was not replaced by one complete retry.");

    retryFetches = 0;
    var partialRejected = false;
    try
    {
        await MediaForgeEpisodeParser.FetchCompleteAsync(
            _ =>
            {
                retryFetches++;
                return Task.FromResult(partialDocument.RootElement.Clone());
            },
            _ => { },
            1,
            2,
            CancellationToken.None);
    }
    catch (MediaForgeException)
    {
        partialRejected = true;
    }

    Assert(
        partialRejected && retryFetches == 2,
        "Two incomplete season responses did not fail closed before queueing.");
}

static void AssertJsonNames(Type type, IReadOnlyDictionary<string, string> expected)
{
    foreach (var pair in expected)
    {
        var property = type.GetProperty(pair.Key)
            ?? throw new InvalidOperationException($"Missing {type.Name}.{pair.Key}.");
        var attribute = property.GetCustomAttributes(typeof(JsonPropertyNameAttribute), inherit: true)
            .Cast<JsonPropertyNameAttribute>()
            .SingleOrDefault();
        Assert(attribute?.Name == pair.Value, $"{type.Name}.{pair.Key} must serialize as {pair.Value}.");
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

static void TestJellixSelectionTokens()
{
    var now = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
    var tokens = new JellixSelectionTokenStore(() => now, TimeSpan.FromMinutes(10));
    var token = tokens.Issue(
        "user-a",
        "series",
        "Example Show",
        "2026",
        "source-a",
        "https://example.invalid/series/example");
    Assert(token.Length is > 0 and <= 100, "Selection token length is incompatible with Jellix.");
    Assert(!token.Contains("Example", StringComparison.OrdinalIgnoreCase), "Selection token contains the media title.");
    Assert(!token.Contains("example.invalid", StringComparison.OrdinalIgnoreCase), "Selection token contains an upstream URL.");
    Assert(!tokens.TryConsumeAny(token + "x", "user-a", out _), "A tampered selection token was accepted.");
    Assert(!tokens.TryConsumeAny(token, "user-b", out _), "Another user consumed a selection token.");
    Assert(tokens.TryConsume(token, "user-a", "series", out var selection), "The owner could not consume a valid token.");
    Assert(selection.Source == "source-a" && selection.MediaType == "series", "Selection token resolved to different media.");
    Assert(!tokens.TryConsumeAny(token, "user-a", out _), "A selection token was replayed.");

    var wrongType = tokens.Issue(
        "user-a",
        "movie",
        "Example Movie",
        "2026",
        "source-a",
        "https://example.invalid/movie/example");
    Assert(!tokens.TryConsume(wrongType, "user-a", "series", out _), "A token crossed its media-type boundary.");
    Assert(tokens.TryConsume(wrongType, "user-a", "movie", out _), "A wrong-type attempt invalidated another valid selection.");

    var expiring = tokens.Issue(
        "user-a",
        "series",
        "Expiring",
        string.Empty,
        "source-a",
        "https://example.invalid/series/expiring");
    now = now.AddMinutes(11);
    Assert(!tokens.TryConsumeAny(expiring, "user-a", out _), "An expired selection token was accepted.");

    var concurrent = tokens.Issue(
        "user-a",
        "series",
        "Concurrent",
        string.Empty,
        "source-a",
        "https://example.invalid/series/concurrent");
    var winners = Enumerable.Range(0, 20)
        .AsParallel()
        .Count(attempt => tokens.TryConsumeAny(concurrent, "user-a", out _));
    Assert(winners == 1, "Concurrent token submission had more than one winner.");
}

static async Task TestJellixBridgeAsync(string testRoot)
{
    var config = new PluginConfiguration
    {
        MediaForgeUrl = "http://mediaforge.invalid:8080",
        MaxPendingRequestsPerUser = 10,
    };
    var environment = CreateApplicationEnvironment(testRoot, "bridge", config);
    var userAId = Guid.NewGuid();
    var userBId = Guid.NewGuid();
    var userA = new User("Server User A", "auth", "reset") { Id = userAId };
    var userB = new User("Server User B", "auth", "reset") { Id = userBId };
    var users = UserManagerProxy.Create([userA, userB]);
    var bridge = new JellixBridge(environment.Application, environment.Client, users);

    var statusJson = await bridge.InvokeAsync(
        "1",
        "status",
        Guid.Empty.ToString("N"),
        "Jellix",
        "{}",
        CancellationToken.None);
    using (var status = JsonDocument.Parse(statusJson))
    {
        Assert(status.RootElement.GetProperty("configured").GetBoolean(), "Bridge status did not detect the configured test connector.");
        Assert(status.RootElement.GetProperty("apiKeyValid").GetBoolean(), "Bridge status incorrectly rejected a valid test API key.");
        Assert(status.RootElement.GetProperty("healthy").GetBoolean(), "Bridge status did not use the sanitized health result.");
        Assert(!statusJson.Contains("test-secret", StringComparison.Ordinal), "Bridge status exposed an API key.");
    }

    await AssertBridgeRejectedAsync(() => bridge.InvokeAsync(
        "2",
        "status",
        Guid.Empty.ToString("N"),
        "Jellix",
        "{}",
        CancellationToken.None), "An unsupported bridge protocol was accepted.");
    await AssertBridgeRejectedAsync(() => bridge.InvokeAsync(
        "1",
        "list",
        Guid.NewGuid().ToString("N"),
        "deleted",
        "{}",
        CancellationToken.None), "A deleted or unknown Jellyfin user was accepted.");
    var disabled = new User("Disabled", "auth", "reset") { Id = Guid.NewGuid() };
    disabled.SetPermission(PermissionKind.IsDisabled, true);
    var disabledBridge = new JellixBridge(
        environment.Application,
        environment.Client,
        UserManagerProxy.Create([disabled]));
    await AssertBridgeRejectedAsync(() => disabledBridge.InvokeAsync(
        "1",
        "list",
        disabled.Id.ToString("N"),
        disabled.Username,
        "{}",
        CancellationToken.None), "A disabled Jellyfin user was accepted.");
    await AssertBridgeRejectedAsync(() => bridge.InvokeAsync(
        "1",
        "list",
        userAId.ToString("D"),
        userA.Username,
        "{}",
        CancellationToken.None), "A non-N Jellyfin user ID was accepted.");
    await AssertBridgeRejectedAsync(() => bridge.InvokeAsync(
        "1",
        "list",
        userAId.ToString("N"),
        userA.Username,
        "{\"unexpected\":true}",
        CancellationToken.None), "An unexpected bridge field was accepted.");
    await AssertBridgeRejectedAsync(() => bridge.InvokeAsync(
        "1",
        "list",
        userAId.ToString("N"),
        userA.Username,
        "{\"nested\":" + new string('[', 10) + "0" + new string(']', 10) + "}",
        CancellationToken.None), "An overly deep bridge payload was accepted.");

    var requestA = new CreateMediaRequest
    {
        Title = "Only A",
        SeriesUrl = "https://example.invalid/series/a",
        Source = "source-a",
        MediaType = "series",
        Episodes = ["https://example.invalid/episode/a"],
        Language = "German Dub",
        Provider = "VOE",
    };
    var requestB = new CreateMediaRequest
    {
        Title = "Only B",
        SeriesUrl = "https://example.invalid/series/b",
        Source = "source-a",
        MediaType = "series",
        Episodes = ["https://example.invalid/episode/b"],
        Language = "German Dub",
        Provider = "VOE",
    };
    await environment.Store.TryAddAsync(userAId.ToString("N"), userA.Username, requestA, RequestStatuses.Pending, 10, CancellationToken.None);
    await environment.Store.TryAddAsync(userBId.ToString("N"), userB.Username, requestB, RequestStatuses.Pending, 10, CancellationToken.None);
    var listJson = await bridge.InvokeAsync("1", "list", userAId.ToString("N"), "spoofed", "{}", CancellationToken.None);
    Assert(listJson.Contains("Only A", StringComparison.Ordinal), "A user could not list their own request.");
    Assert(!listJson.Contains("Only B", StringComparison.Ordinal), "A user could list another user's request.");

    var submissionToken = environment.Tokens.Issue(
        userAId.ToString("N"),
        "series",
        "Submitted Through Bridge",
        "2026",
        "source-a",
        "https://example.invalid/series/bridge-submit");
    var submitJson = await bridge.InvokeAsync(
        "1",
        "submit",
        userAId.ToString("N"),
        "spoofed-admin",
        JsonSerializer.Serialize(new { selectionToken = submissionToken }),
        CancellationToken.None);
    using (var submit = JsonDocument.Parse(submitJson))
    {
        Assert(submit.RootElement.GetProperty("status").GetString() == "pending", "Bridge submit returned an unstable status.");
    }

    var stored = (await environment.Store.ListForUserAsync(userAId.ToString("N"), 500, CancellationToken.None))
        .Single(item => item.Title == "Submitted Through Bridge");
    Assert(stored.Username == userA.Username, "Bridge submission trusted the caller-supplied username.");

    foreach (var mapping in new[]
    {
        (RequestStatuses.Pending, false, "pending"),
        (RequestStatuses.Processing, false, "pending"),
        (RequestStatuses.Queued, false, "queued"),
        (RequestStatuses.Queued, true, "downloading"),
        (RequestStatuses.Completed, false, "available"),
        (RequestStatuses.Available, false, "available"),
        (RequestStatuses.Partial, false, "failed"),
        (RequestStatuses.Failed, false, "failed"),
        (RequestStatuses.Cancelled, false, "failed"),
        (RequestStatuses.Rejected, false, "rejected"),
        (RequestStatuses.Withdrawn, false, "withdrawn"),
    })
    {
        Assert(
            JellixBridge.MapStatus(new MediaRequest { Status = mapping.Item1, QueueRunning = mapping.Item2 }) == mapping.Item3,
            $"Bridge status mapping for {mapping.Item1} is unstable.");
    }
}

static async Task TestSharedApplicationRulesAsync(string testRoot)
{
    var config = new PluginConfiguration
    {
        MediaForgeUrl = "http://mediaforge.invalid:8080",
        MaxPendingRequestsPerUser = 10,
    };
    var environment = CreateApplicationEnvironment(testRoot, "shared-rules", config);
    var sources = await environment.Application.GetAllowedSourcesAsync("user-a", CancellationToken.None, applyRateLimit: false);
    Assert(sources.Select(item => item.Id).SequenceEqual(["source-a"]), "Disabled or adult sources crossed the shared source policy.");

    environment.Handler.HealthStatus = System.Net.HttpStatusCode.Unauthorized;
    var invalidKey = await environment.Client.CheckHealthAsync(CancellationToken.None);
    Assert(invalidKey.Configured && !invalidKey.Healthy && !invalidKey.ApiKeyValid, "A 401 health response was not classified as an invalid API key.");
    environment.Handler.HealthStatus = System.Net.HttpStatusCode.InternalServerError;
    var unavailable = await environment.Client.CheckHealthAsync(CancellationToken.None);
    Assert(unavailable.Configured && !unavailable.Healthy && unavailable.ApiKeyValid, "A non-authentication health failure was misreported as an invalid key.");
    environment.Handler.HealthStatus = System.Net.HttpStatusCode.OK;

    var search = await environment.Application.SearchAsync(
        "user-a",
        "Example",
        "all",
        "series",
        issueSelectionTokens: true,
        CancellationToken.None);
    Assert(search.Items.Count == 1, "Shared Jellix search did not return the safe source result.");
    Assert(search.Items[0].Token.Length <= 100, "Shared search returned an incompatible selection token.");
    var serializedSearch = JsonSerializer.Serialize(new { items = search.Items });
    Assert(!serializedSearch.Contains("example.invalid", StringComparison.OrdinalIgnoreCase), "Jellix search exposed an upstream URL.");
    Assert(!serializedSearch.Contains("source-a", StringComparison.OrdinalIgnoreCase), "Jellix search exposed a MediaForge source identifier.");

    config.MaintenanceMode = true;
    var maintenanceToken = environment.Tokens.Issue(
        "maintenance-user",
        "series",
        "Maintenance",
        string.Empty,
        "source-a",
        "https://example.invalid/series/maintenance");
    var maintenanceRejected = false;
    try
    {
        await environment.Application.SubmitSelectionAsync(
            "maintenance-user",
            "Maintenance User",
            maintenanceToken,
            CancellationToken.None);
    }
    catch (MediaForgeApplicationException exception) when (exception.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
    {
        maintenanceRejected = true;
    }

    Assert(maintenanceRejected, "Jellix submission bypassed connector maintenance mode.");
    config.MaintenanceMode = false;

    var duplicateTokens = Enumerable.Range(0, 2).Select(_ => environment.Tokens.Issue(
        "duplicate-user",
        "series",
        "Duplicate Show",
        "2026",
        "source-a",
        "https://example.invalid/series/duplicate")).ToArray();
    var duplicateResults = await Task.WhenAll(duplicateTokens.Select(token => environment.Application.SubmitSelectionAsync(
        "duplicate-user",
        "Duplicate User",
        token,
        CancellationToken.None)));
    Assert(duplicateResults.Count(item => item.Disposition == SubmitDisposition.Stored) == 1, "Concurrent shared submission did not store exactly one request.");
    Assert(duplicateResults.Count(item => item.Disposition == SubmitDisposition.Duplicate) == 1, "Concurrent shared submission did not report its duplicate.");

    var limitedConfig = new PluginConfiguration
    {
        MediaForgeUrl = "http://mediaforge.invalid:8080",
        MaxPendingRequestsPerUser = 1,
    };
    var limited = CreateApplicationEnvironment(testRoot, "user-limit", limitedConfig);
    foreach (var index in Enumerable.Range(1, 2))
    {
        var token = limited.Tokens.Issue(
            "limited-user",
            "series",
            $"Limited {index}",
            string.Empty,
            "source-a",
            $"https://example.invalid/series/limited-{index}");
        var result = await limited.Application.SubmitSelectionAsync("limited-user", "Limited", token, CancellationToken.None);
        Assert(
            index == 1 ? result.Disposition == SubmitDisposition.Stored : result.Disposition == SubmitDisposition.LimitReached,
            "Shared submission did not apply the configured per-user limit.");
    }

    var capacity = CreateApplicationEnvironment(testRoot, "store-capacity", config, maxStoredRequests: 1);
    foreach (var index in Enumerable.Range(1, 2))
    {
        var token = capacity.Tokens.Issue(
            "capacity-user",
            "series",
            $"Capacity {index}",
            string.Empty,
            "source-a",
            $"https://example.invalid/series/capacity-{index}");
        var result = await capacity.Application.SubmitSelectionAsync("capacity-user", "Capacity", token, CancellationToken.None);
        Assert(
            index == 1 ? result.Disposition == SubmitDisposition.Stored : result.Disposition == SubmitDisposition.StoreCapacityReached,
            "Shared submission did not enforce request-store capacity.");
    }
}

static ApplicationTestEnvironment CreateApplicationEnvironment(
    string testRoot,
    string name,
    PluginConfiguration configuration,
    int maxStoredRequests = 20_000)
{
    var root = Path.Combine(testRoot, name);
    var secret = new SecretStore(Path.Combine(root, "secrets"));
    secret.SetApiKey("test-secret-key");
    var handler = new FakeMediaForgeHandler();
    var client = new MediaForgeClient(new HttpClient(handler), secret, () => configuration);
    var store = new RequestStore(Path.Combine(root, "requests"), maxStoredRequests, 64L * 1024 * 1024);
    var tokens = new JellixSelectionTokenStore();
    var application = new MediaForgeRequestApplicationService(
        client,
        store,
        new MediaAccessGrantStore(),
        new UserRateLimiter(),
        new JellyfinLibraryAvailabilityService(LibraryManagerProxy.Create(_ => [])),
        tokens,
        () => configuration);
    return new ApplicationTestEnvironment(application, client, store, tokens, handler);
}

static async Task AssertBridgeRejectedAsync(Func<Task<string>> action, string message)
{
    try
    {
        _ = await action();
    }
    catch (MediaForgeApplicationException)
    {
        return;
    }

    throw new InvalidOperationException(message);
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
    await store.MarkQueuedAsync(
        third.Request.Id,
        42,
        "Admin",
        "Safe count-only warning",
        CancellationToken.None);
    Assert(
        (await store.GetAsync(third.Request.Id, CancellationToken.None))?.Error == "Safe count-only warning",
        "A queue-count mismatch warning was not persisted without retrying the submission.");
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

public sealed record ApplicationTestEnvironment(
    MediaForgeRequestApplicationService Application,
    MediaForgeClient Client,
    RequestStore Store,
    JellixSelectionTokenStore Tokens,
    FakeMediaForgeHandler Handler);

public sealed class FakeMediaForgeHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    public System.Net.HttpStatusCode HealthStatus { get; set; } = System.Net.HttpStatusCode.OK;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        if (!request.Headers.TryGetValues("X-Api-Key", out var values)
            || values.SingleOrDefault() != "test-secret-key")
        {
            return Json(System.Net.HttpStatusCode.Unauthorized, "{\"error\":\"authentication required\"}");
        }

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        return path switch
        {
            "/api/v1/connector/health" => Json(
                HealthStatus,
                HealthStatus == System.Net.HttpStatusCode.OK ? "{\"ok\":true}" : "{\"error\":\"safe\"}"),
            "/api/v1/connector/sources" => Json(System.Net.HttpStatusCode.OK, """
                {
                  "sources": [
                    {"id":"source-a","label":"Safe Source","adult":false,"enabled":true,"media_types":["series"]},
                    {"id":"adult","label":"Adult","adult":true,"enabled":true,"media_types":["series"]},
                    {"id":"disabled","label":"Disabled","adult":false,"enabled":false,"media_types":["series"]}
                  ]
                }
                """),
            "/api/v1/connector/search" => Json(System.Net.HttpStatusCode.OK, """
                {"results":[{"title":"Search Result","url":"https://example.invalid/series/search-result","year":"2026","media_type":"series"}]}
                """),
            "/api/v1/connector/series" => Json(System.Net.HttpStatusCode.OK, """
                {"title":"Submitted Through Bridge","description":"Safe","is_movie":false,"year":2026,"tvdb_id":"12345"}
                """),
            "/api/v1/connector/seasons" => Json(System.Net.HttpStatusCode.OK, """
                {"seasons":[{"url":"https://example.invalid/season/1","season_number":1,"episode_count":1}]}
                """),
            "/api/v1/connector/episodes" => Json(System.Net.HttpStatusCode.OK, """
                {"episodes":[{"url":"https://example.invalid/episode/1","season_number":1,"episode_number":1,"languages":["German Dub"]}]}
                """),
            "/api/v1/connector/providers" => Json(System.Net.HttpStatusCode.OK, "{\"German Dub\":[\"VOE\"]}"),
            "/api/v1/connector/progress" => Json(System.Net.HttpStatusCode.OK, "{\"items\":[]}"),
            "/api/v1/connector/download" => Json(System.Net.HttpStatusCode.OK, "{\"queue_id\":42,\"accepted_episode_count\":1}"),
            _ => Json(System.Net.HttpStatusCode.NotFound, "{\"error\":\"not found\"}"),
        };
    }

    private static Task<HttpResponseMessage> Json(System.Net.HttpStatusCode status, string json)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
}

public class UserManagerProxy : DispatchProxy
{
    private IReadOnlyDictionary<Guid, User> _users = new Dictionary<Guid, User>();

    public static IUserManager Create(IEnumerable<User> users)
    {
        var manager = Create<IUserManager, UserManagerProxy>();
        ((UserManagerProxy)(object)manager)._users = users.ToDictionary(user => user.Id);
        return manager;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IUserManager.GetUserById)
            && args is { Length: 1 }
            && args[0] is Guid id)
        {
            return _users.GetValueOrDefault(id);
        }

        throw new NotSupportedException($"Unexpected IUserManager call: {targetMethod?.Name}");
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
