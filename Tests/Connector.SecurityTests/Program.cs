using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MediaForge.Configuration;
using Jellyfin.Plugin.MediaForge.Models;
using Jellyfin.Plugin.MediaForge.Services;

var testRoot = Path.Combine(Path.GetTempPath(), "mediaforge-connector-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    TestSecretStore(testRoot);
    TestConfigurationSerialization();
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
