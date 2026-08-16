using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaForge.Models;

/// <summary>Persistent user request and its MediaForge queue result.</summary>
public sealed class MediaRequest
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("seriesUrl")]
    public string SeriesUrl { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = "series";

    [JsonPropertyName("selectionLabel")]
    public string SelectionLabel { get; set; } = string.Empty;

    [JsonPropertyName("episodesJson")]
    public string EpisodesJson { get; set; } = "[]";

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("upscale")]
    public bool Upscale { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = RequestStatuses.Pending;

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; }

    [JsonPropertyName("decidedUtc")]
    public DateTime? DecidedUtc { get; set; }

    [JsonPropertyName("decidedBy")]
    public string? DecidedBy { get; set; }

    [JsonPropertyName("mediaForgeQueueId")]
    public long? MediaForgeQueueId { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("episodes")]
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

    /// <summary>Gets or sets transient synchronized progress for in-process consumers.</summary>
    [JsonIgnore]
    public int? Progress { get; set; }

    /// <summary>Gets or sets whether MediaForge reports an actively running queue item.</summary>
    [JsonIgnore]
    public bool QueueRunning { get; set; }
}

/// <summary>Known request status values.</summary>
public static class RequestStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Queued = "queued";
    public const string Completed = "completed";
    public const string Available = "available";
    public const string Partial = "partial";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";
    public const string Failed = "failed";
}
