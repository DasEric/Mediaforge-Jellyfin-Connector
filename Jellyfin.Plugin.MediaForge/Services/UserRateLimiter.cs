namespace Jellyfin.Plugin.MediaForge.Services;

/// <summary>Small per-user fixed-window limiter for expensive connector operations.</summary>
public sealed class UserRateLimiter
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public bool TryConsume(string userId, string operation, int limit, TimeSpan duration)
    {
        var now = DateTime.UtcNow;
        var key = userId + "\n" + operation;
        lock (_sync)
        {
            if (_windows.Count > 10_000)
            {
                foreach (var expired in _windows.Where(item => item.Value.StartUtc + item.Value.Duration <= now).Select(item => item.Key).ToArray())
                {
                    _windows.Remove(expired);
                }
            }

            if (!_windows.TryGetValue(key, out var window) || window.StartUtc + window.Duration <= now)
            {
                _windows[key] = new Window(now, duration, 1);
                return true;
            }

            if (window.Count >= limit)
            {
                return false;
            }

            _windows[key] = window with { Count = window.Count + 1 };
            return true;
        }
    }

    private sealed record Window(DateTime StartUtc, TimeSpan Duration, int Count);
}
