namespace Jellyfin.Plugin.MediaForge.Helpers;

/// <summary>Payload supplied by the File Transformation plugin.</summary>
public sealed class PatchRequestPayload
{
    public string? Contents { get; set; }
}

