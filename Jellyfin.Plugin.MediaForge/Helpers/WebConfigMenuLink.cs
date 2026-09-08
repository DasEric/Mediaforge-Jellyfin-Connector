using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.MediaForge.Helpers;

/// <summary>Registers a custom link in Jellyfin 12's native web navigation.</summary>
public static class WebConfigMenuLink
{
    private const int LockTimeoutSeconds = 5;

    public static bool UpdateFile(string path, string name, string icon, string url, bool enabled)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var mutexName = "JellyfinPluginMenuLinks-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
        using var mutex = new Mutex(initiallyOwned: false, mutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(LockTimeoutSeconds));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                throw new IOException("Timed out while waiting to update Jellyfin web navigation.");
            }

            var original = File.ReadAllText(fullPath);
            var updated = ApplyToJson(original, name, icon, url, enabled);
            if (string.Equals(original, updated, StringComparison.Ordinal))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new IOException("The Jellyfin web configuration path has no parent directory.");
            var temporary = Path.Combine(directory, Path.GetRandomFileName());
            try
            {
                File.WriteAllText(temporary, updated);
                File.Move(temporary, fullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return true;
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    public static string ApplyToJson(string source, string name, string icon, string url, bool enabled)
    {
        var root = JObject.Parse(source);
        var menuLinksToken = root["menuLinks"];
        if (menuLinksToken is not null && menuLinksToken.Type is not JTokenType.Null && menuLinksToken is not JArray)
        {
            throw new InvalidDataException("Jellyfin web config property 'menuLinks' is not an array.");
        }

        var menuLinks = menuLinksToken as JArray;
        var matches = menuLinks?
            .OfType<JObject>()
            .Where(item => string.Equals((string?)item["url"], url, StringComparison.Ordinal))
            .ToArray() ?? [];

        if (!enabled)
        {
            if (matches.Length == 0)
            {
                return source;
            }

            foreach (var match in matches)
            {
                match.Remove();
            }

            return root.ToString(Formatting.Indented) + Environment.NewLine;
        }

        var desired = new JObject
        {
            ["name"] = name,
            ["icon"] = icon,
            ["url"] = url,
        };

        if (matches.Length == 1 && JToken.DeepEquals(matches[0], desired))
        {
            return source;
        }

        menuLinks ??= new JArray();
        if (menuLinksToken is null || menuLinksToken.Type == JTokenType.Null)
        {
            root["menuLinks"] = menuLinks;
        }

        var insertionIndex = matches.Length > 0 ? menuLinks.IndexOf(matches[0]) : menuLinks.Count;
        foreach (var match in matches)
        {
            match.Remove();
        }

        menuLinks.Insert(Math.Min(insertionIndex, menuLinks.Count), desired);
        return root.ToString(Formatting.Indented) + Environment.NewLine;
    }
}
