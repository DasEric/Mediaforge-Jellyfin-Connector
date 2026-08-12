using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.MediaForge.Models;

/// <summary>Payload used by a Jellyfin user to request content.</summary>
public sealed class CreateMediaRequest
{
    [Required]
    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(2048)]
    public string SeriesUrl { get; set; } = string.Empty;

    [MaxLength(80)]
    public string Source { get; set; } = string.Empty;

    [MaxLength(20)]
    public string MediaType { get; set; } = "series";

    [MaxLength(300)]
    public string SelectionLabel { get; set; } = string.Empty;

    [Required]
    public List<string> Episodes { get; set; } = [];

    [Required]
    [MaxLength(100)]
    public string Language { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Provider { get; set; } = string.Empty;

    public bool Upscale { get; set; }
}

/// <summary>Optional rejection reason supplied by an administrator.</summary>
public sealed class RejectMediaRequest
{
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Payload used by an administrator to replace the MediaForge API key.</summary>
public sealed class UpdateApiKeyRequest
{
    [Required]
    [MaxLength(512)]
    public string ApiKey { get; set; } = string.Empty;
}
