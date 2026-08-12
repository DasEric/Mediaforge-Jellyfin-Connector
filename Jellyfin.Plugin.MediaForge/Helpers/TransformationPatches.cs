using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MediaForge.Helpers;

/// <summary>File Transformation callbacks for the Jellyfin web client.</summary>
public static class TransformationPatches
{
    private const string PluginName = "MediaForge Requests";

    public static string IndexHtml(PatchRequestPayload content)
    {
        var source = content.Contents ?? string.Empty;
        var updated = RemoveScript(source);
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

