using System.Text.Json;

namespace Jellyfin.Plugin.MediaForge.Models;

/// <summary>Persistent user request and its MediaForge queue result.</summary>
public sealed class MediaRequest
{
    public long Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string SeriesUrl { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string MediaType { get; set; } = "series";

    public string SelectionLabel { get; set; } = string.Empty;

    public string EpisodesJson { get; set; } = "[]";

    public string Language { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public bool Upscale { get; set; }

    public string Status { get; set; } = RequestStatuses.Pending;

    public DateTime CreatedUtc { get; set; }

    public DateTime? DecidedUtc { get; set; }

    public string? DecidedBy { get; set; }

    public long? MediaForgeQueueId { get; set; }

    public string? Error { get; set; }

    public IReadOnlyList<string> Episodes
    {
        get
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(EpisodesJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}

/// <summary>Known request status values.</summary>
public static class RequestStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Queued = "queued";
    public const string Completed = "completed";
    public const string Partial = "partial";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";
    public const string Failed = "failed";
}
