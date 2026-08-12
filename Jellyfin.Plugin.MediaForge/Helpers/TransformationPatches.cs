using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaForge.Helpers;

/// <summary>File Transformation callbacks for the Jellyfin web client.</summary>
public static class TransformationPatches
{
    private const string PluginName = "MediaForge Requests";

    public static string IndexHtml(PatchRequestPayload content)
        => ApplyIndexHtml(content, Plugin.Instance?.Configuration.EnableAllUsers == true);

    /// <summary>Applies or removes the user-navigation injection idempotently.</summary>
    public static string ApplyIndexHtml(PatchRequestPayload content, bool enabled)
    {
        var source = content.Contents ?? string.Empty;
        var updated = RemoveScript(source);
        if (!enabled)
        {
            return updated;
        }

        var script = $"<script plugin=\"{PluginName}\" src=\"../MediaForgeRequests/InjectionScript\" defer></script>";
        return updated.Contains("</body>", StringComparison.OrdinalIgnoreCase)
            ? Regex.Replace(updated, "</body>", script + "\n</body>", RegexOptions.IgnoreCase)
            : updated;
    }

    public static string RemoveScript(string content)
    {
        var expression = $"<script[^>]*plugin=[\"']{Regex.Escape(PluginName)}[\"'][^>]*>\\s*</script>\\s*";
        return Regex.Replace(content ?? string.Empty, expression, string.Empty, RegexOptions.IgnoreCase);
    }
}
