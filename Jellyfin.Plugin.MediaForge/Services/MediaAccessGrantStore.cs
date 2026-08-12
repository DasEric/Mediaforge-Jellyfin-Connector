using System.Text.Json;

namespace Jellyfin.Plugin.MediaForge.Services;

/// <summary>
/// Tracks short-lived URLs returned by MediaForge so clients cannot inject
/// arbitrary download targets into later connector calls.
/// </summary>
public sealed class MediaAccessGrantStore
{
    private const int MaxEntriesPerUser = 6000;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly object _sync = new();
    private readonly Dictionary<string, Dictionary<string, Grant>> _users = new(StringComparer.Ordinal);

    public void GrantFromJson(string userId, string source, JsonElement value)
    {
        var urls = new HashSet<string>(StringComparer.Ordinal);
        CollectUrls(value, urls);
        GrantUrls(userId, source, urls);
    }

    public void GrantUrl(string userId, string source, string url)
        => GrantUrls(userId, source, [url]);

    public bool IsGranted(string userId, string url, out string source)
    {
        source = string.Empty;
        if (!TryNormalizeUrl(url, out var normalized))
        {
            return false;
        }

        lock (_sync)
        {
            PurgeExpiredLocked(userId, DateTime.UtcNow);
            if (!_users.TryGetValue(userId, out var grants)
                || !grants.TryGetValue(normalized, out var grant))
            {
                return false;
            }

            source = grant.Source;
            return true;
        }
    }

    public bool AreGranted(string userId, string expectedSource, IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            if (!IsGranted(userId, url, out var source)
                || !string.Equals(source, expectedSource, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void GrantUrls(string userId, string source, IEnumerable<string> urls)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var now = DateTime.UtcNow;
        lock (_sync)
        {
            PurgeExpiredLocked(userId, now);
            if (!_users.TryGetValue(userId, out var grants))
            {
                grants = new Dictionary<string, Grant>(StringComparer.Ordinal);
                _users[userId] = grants;
            }

            foreach (var url in urls)
            {
                if (TryNormalizeUrl(url, out var normalized))
                {
                    grants[normalized] = new Grant(source, now.Add(Lifetime), now);
                }
            }

            if (grants.Count > MaxEntriesPerUser)
            {
                foreach (var key in grants
                    .OrderBy(item => item.Value.CreatedUtc)
                    .Take(grants.Count - MaxEntriesPerUser)
                    .Select(item => item.Key)
                    .ToArray())
                {
                    grants.Remove(key);
                }
            }
        }
    }

    private void PurgeExpiredLocked(string userId, DateTime now)
    {
        if (!_users.TryGetValue(userId, out var grants))
        {
            return;
        }

        foreach (var key in grants.Where(item => item.Value.ExpiresUtc <= now).Select(item => item.Key).ToArray())
        {
            grants.Remove(key);
        }

        if (grants.Count == 0)
        {
            _users.Remove(userId);
        }
    }

    private static void CollectUrls(JsonElement value, ISet<string> urls)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Name is "url" or "link" or "series_url")
                {
                    var candidate = property.Value.GetString();
                    if (candidate is not null)
                    {
                        urls.Add(candidate);
                    }
                }
                else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    CollectUrls(property.Value, urls);
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                CollectUrls(item, urls);
            }
        }
    }

    internal static bool TryNormalizeUrl(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        normalized = builder.Uri.AbsoluteUri;
        return true;
    }

    private sealed record Grant(string Source, DateTime ExpiresUtc, DateTime CreatedUtc);
}
