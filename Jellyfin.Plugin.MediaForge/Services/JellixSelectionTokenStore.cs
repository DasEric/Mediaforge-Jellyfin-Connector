using System.Security.Cryptography;

namespace Jellyfin.Plugin.MediaForge.Services;

/// <summary>
/// Stores short-lived, opaque MediaForge search selections for the optional
/// Jellix bridge. Tokens never contain upstream URLs or other selection data.
/// </summary>
public sealed class JellixSelectionTokenStore
{
    private const int MaxEntries = 10_000;
    private const int MaxEntriesPerUser = 100;
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);
    private readonly object _sync = new();
    private readonly Dictionary<string, Selection> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _lifetime;

    public JellixSelectionTokenStore()
        : this(() => DateTime.UtcNow, DefaultLifetime)
    {
    }

    internal JellixSelectionTokenStore(Func<DateTime> utcNow, TimeSpan lifetime)
    {
        _utcNow = utcNow;
        _lifetime = lifetime;
    }

    public string Issue(
        string userId,
        string mediaType,
        string title,
        string year,
        string source,
        string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!MediaAccessGrantStore.TryNormalizeUrl(url, out var normalizedUrl))
        {
            throw new ArgumentException("Invalid selection URL.", nameof(url));
        }

        var now = _utcNow();
        lock (_sync)
        {
            PurgeExpiredLocked(now);
            foreach (var existing in _entries
                .Where(item => string.Equals(item.Value.UserId, userId, StringComparison.Ordinal))
                .OrderBy(item => item.Value.CreatedUtc)
                .Take(Math.Max(0, CountForUserLocked(userId) - MaxEntriesPerUser + 1))
                .Select(item => item.Key)
                .ToArray())
            {
                _entries.Remove(existing);
            }

            while (_entries.Count >= MaxEntries)
            {
                var oldest = _entries.MinBy(item => item.Value.CreatedUtc);
                if (string.IsNullOrEmpty(oldest.Key))
                {
                    break;
                }

                _entries.Remove(oldest.Key);
            }

            string token;
            do
            {
                token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
            }
            while (_entries.ContainsKey(token));

            _entries[token] = new Selection(
                userId,
                mediaType,
                title,
                year,
                source,
                normalizedUrl,
                now,
                now.Add(_lifetime));
            return token;
        }
    }

    /// <summary>
    /// Atomically consumes a token. A failed downstream operation deliberately
    /// requires a new search so a captured token can never be replayed.
    /// </summary>
    public bool TryConsume(string token, string userId, string mediaType, out JellixSelection selection)
    {
        selection = default!;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return false;
        }

        lock (_sync)
        {
            PurgeExpiredLocked(_utcNow());
            if (!_entries.TryGetValue(token, out var stored)
                || !string.Equals(stored.UserId, userId, StringComparison.Ordinal)
                || !string.Equals(stored.MediaType, mediaType, StringComparison.Ordinal))
            {
                return false;
            }

            _entries.Remove(token);
            selection = new JellixSelection(
                stored.MediaType,
                stored.Title,
                stored.Year,
                stored.Source,
                stored.Url);
            return true;
        }
    }

    public bool TryConsumeAny(string token, string userId, out JellixSelection selection)
    {
        selection = default!;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return false;
        }

        lock (_sync)
        {
            PurgeExpiredLocked(_utcNow());
            if (!_entries.TryGetValue(token, out var stored)
                || !string.Equals(stored.UserId, userId, StringComparison.Ordinal))
            {
                return false;
            }

            _entries.Remove(token);
            selection = new JellixSelection(
                stored.MediaType,
                stored.Title,
                stored.Year,
                stored.Source,
                stored.Url);
            return true;
        }
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                PurgeExpiredLocked(_utcNow());
                return _entries.Count;
            }
        }
    }

    private int CountForUserLocked(string userId)
        => _entries.Count(item => string.Equals(item.Value.UserId, userId, StringComparison.Ordinal));

    private void PurgeExpiredLocked(DateTime now)
    {
        foreach (var token in _entries
            .Where(item => item.Value.ExpiresUtc <= now)
            .Select(item => item.Key)
            .ToArray())
        {
            _entries.Remove(token);
        }
    }

    private sealed record Selection(
        string UserId,
        string MediaType,
        string Title,
        string Year,
        string Source,
        string Url,
        DateTime CreatedUtc,
        DateTime ExpiresUtc);
}

/// <summary>Validated MediaForge selection resolved from an opaque token.</summary>
public sealed record JellixSelection(
    string MediaType,
    string Title,
    string Year,
    string Source,
    string Url);
