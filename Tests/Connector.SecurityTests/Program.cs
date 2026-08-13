using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MediaForge;
using Jellyfin.Plugin.MediaForge.Configuration;
using Jellyfin.Plugin.MediaForge.Api;
using Jellyfin.Plugin.MediaForge.Helpers;
using Jellyfin.Plugin.MediaForge.Models;
using Jellyfin.Plugin.MediaForge.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;

var testRoot = Path.Combine(Path.GetTempPath(), "mediaforge-connector-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    TestSecretStore(testRoot);
    TestConfigurationSerialization();
    TestAuthorizationBoundaries();
    TestApiJsonContracts();
    TestPosterProxyContract();
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
}

static void TestPosterProxyContract()
{
    var method = typeof(MediaForgeRequestsController).GetMethod(
        "TryReadMediaForgeImageUrl",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing poster URL validator.");
    object?[] connectorArgs =
    [
        "/api/v1/connector/image?url=https%3A%2F%2Fimages.example.invalid%2Fposter.jpg",
        null,
    ];
    Assert((bool)method.Invoke(null, connectorArgs)!, "The connector image route was rejected.");
    Assert(
        connectorArgs[1] as string == "https://images.example.invalid/poster.jpg",
        "The connector image route did not decode to the expected upstream URL.");

    object?[] legacyArgs =
    [
        "/api/img?url=https%3A%2F%2Fimages.example.invalid%2Fposter.jpg",
        null,
    ];
    Assert(!(bool)method.Invoke(null, legacyArgs)!, "The obsolete MediaForge Web UI image route was still accepted.");

    var clientSource = File.ReadAllText(
        Path.Combine("Jellyfin.Plugin.MediaForge", "Services", "MediaForgeClient.cs"));
    Assert(
        clientSource.Contains("include_adult = AllowAdultSources()", StringComparison.Ordinal),
        "Jellyfin searches do not forward the administrator Adult-source setting.");
    Assert(
        clientSource.Contains("sources?include_adult=", StringComparison.Ordinal),
        "Jellyfin source lists do not forward the administrator Adult-source setting.");
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
    Assert(afterCompletion.Request is not null, "A completed download continued to block a future request.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
