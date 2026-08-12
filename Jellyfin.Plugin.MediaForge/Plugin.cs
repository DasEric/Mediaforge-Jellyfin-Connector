using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MediaForge.Configuration;
using Jellyfin.Plugin.MediaForge.Helpers;
using Jellyfin.Plugin.MediaForge.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.MediaForge;

/// <summary>Jellyfin plugin entry point.</summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "2ea7f67d-8e4d-4c84-bd5a-a5bcd713bb23";
    private const string PluginDisplayName = "MediaForge Requests";
    private const int TransformationRetries = 30;
    private readonly IApplicationPaths _applicationPaths;
    private int _transformationRegistered;
    private int _transformationRegistrationInProgress;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationPaths = applicationPaths;
        Secrets = new SecretStore(DataFolderPath);

        if (MigrateLegacySecret(Configuration))
        {
            SaveConfiguration(Configuration);
        }

        if (Configuration.EnableAllUsers)
        {
            // Make the user entry available immediately. Registering the optional
            // File Transformation integration still happens in the background so
            // the injection survives future jellyfin-web file replacements.
            EnableWebInjection();
        }
        else
        {
            CleanupInjection();
        }
    }

    public static Plugin? Instance { get; private set; }

    public SecretStore Secrets { get; }

    public override string Name => PluginDisplayName;

    public override string Description => "MediaForge-Suche und Download-Anfragen für alle Jellyfin-Benutzer.";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is not PluginConfiguration typed)
        {
            throw new ArgumentException("Unexpected plugin configuration type.", nameof(configuration));
        }

        MigrateLegacySecret(typed);
        base.UpdateConfiguration(typed);

        if (typed.EnableAllUsers)
        {
            EnableWebInjection();
        }
        else
        {
            CleanupInjection();
        }
    }

    private string IndexHtmlPath => Path.Combine(_applicationPaths.WebPath, "index.html");

    private void EnableWebInjection()
    {
        UpdateIndexHtml(inject: true);
        if (Volatile.Read(ref _transformationRegistered) == 0
            && Interlocked.CompareExchange(ref _transformationRegistrationInProgress, 1, 0) == 0)
        {
            _ = Task.Run(RegisterWebInjectionAsync);
        }
    }

    private async Task RegisterWebInjectionAsync()
    {
        try
        {
            for (var attempt = 0; attempt < TransformationRetries; attempt++)
            {
                if (Plugin.Instance?.Configuration.EnableAllUsers != true)
                {
                    return;
                }

                try
                {
                    if (TryRegisterFileTransformation())
                    {
                        Volatile.Write(ref _transformationRegistered, 1);
                        return;
                    }
                }
                catch
                {
                    // The other plugin may still be initializing.
                }

                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }

            if (Plugin.Instance?.Configuration.EnableAllUsers == true)
            {
                UpdateIndexHtml(inject: true);
            }
        }
        finally
        {
            Volatile.Write(ref _transformationRegistrationInProgress, 0);
        }
    }

    private bool TryRegisterFileTransformation()
    {
        var assembly = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(candidate => candidate.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) == true);
        var interfaceType = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        if (interfaceType is null)
        {
            return false;
        }

        var registerMethod = interfaceType.GetMethod("RegisterTransformation");
        if (registerMethod is null)
        {
            return false;
        }

        var payload = new JObject
        {
            { "id", PluginGuid },
            { "fileNamePattern", "index.html" },
            { "callbackAssembly", GetType().Assembly.FullName },
            { "callbackClass", typeof(TransformationPatches).FullName },
            { "callbackMethod", nameof(TransformationPatches.IndexHtml) },
        };
        registerMethod.Invoke(null, [payload]);
        return true;
    }

    private void UpdateIndexHtml(bool inject)
    {
        try
        {
            if (!File.Exists(IndexHtmlPath))
            {
                return;
            }

            var original = File.ReadAllText(IndexHtmlPath);
            var content = TransformationPatches.RemoveScript(original);
            if (inject && content.Contains("</body>", StringComparison.OrdinalIgnoreCase))
            {
                const string script = "<script plugin=\"MediaForge Requests\" src=\"../MediaForgeRequests/InjectionScript\" defer></script>";
                content = Regex.Replace(content, "</body>", script + "\n</body>", RegexOptions.IgnoreCase);
            }

            if (string.Equals(original, content, StringComparison.Ordinal))
            {
                return;
            }

            var directory = Path.GetDirectoryName(IndexHtmlPath);
            if (directory is null)
            {
                return;
            }

            var temporary = Path.Combine(directory, Path.GetRandomFileName());
            try
            {
                File.WriteAllText(temporary, content);
                File.Move(temporary, IndexHtmlPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        catch
        {
            // Web injection is best effort; the dashboard page remains usable.
        }
    }

    public void CleanupInjection() => UpdateIndexHtml(inject: false);

    private bool MigrateLegacySecret(PluginConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.MediaForgeApiKey))
        {
            return false;
        }

        Secrets.SetApiKey(configuration.MediaForgeApiKey);
        configuration.MediaForgeApiKey = string.Empty;
        return true;
    }

    public override void OnUninstalling()
    {
        CleanupInjection();
        base.OnUninstalling();
    }

    public IEnumerable<PluginPageInfo> GetPages()
        => CreatePages();

    /// <summary>Creates the embedded page registrations in Jellyfin's preferred order.</summary>
    public static IReadOnlyList<PluginPageInfo> CreatePages()
    {
        var root = typeof(Plugin).Namespace;
        return
        [
            new PluginPageInfo
            {
                Name = "MediaForgeRequestsConfig",
                EmbeddedResourcePath = $"{root}.Web.config.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "settings",
                DisplayName = "MediaForge Requests Settings",
            },
            new PluginPageInfo
            {
                Name = "MediaForgeRequestsConfigJS",
                EmbeddedResourcePath = $"{root}.Web.config.js",
            },
            new PluginPageInfo
            {
                Name = "MediaForgeRequests",
                EmbeddedResourcePath = $"{root}.Web.requests.html",
                DisplayName = PluginDisplayName,
            },
            new PluginPageInfo
            {
                Name = "MediaForgeRequestsJS",
                EmbeddedResourcePath = $"{root}.Web.requests.js",
            },
        ];
    }
}
