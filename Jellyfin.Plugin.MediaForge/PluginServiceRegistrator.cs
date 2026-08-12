using Jellyfin.Plugin.MediaForge.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaForge;

/// <summary>Registers connector services with Jellyfin's DI container.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection
            .AddHttpClient<MediaForgeClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(90);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-MediaForge-Requests/0.2.2");
            })
            .RedactLoggedHeaders(["X-Api-Key"])
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Never forward the custom API-key header through an upstream redirect.
                AllowAutoRedirect = false,
                UseCookies = false,
                MaxConnectionsPerServer = 16,
            });
        serviceCollection.AddSingleton<RequestStore>();
        serviceCollection.AddSingleton<MediaAccessGrantStore>();
        serviceCollection.AddSingleton<UserRateLimiter>();
        serviceCollection.AddSingleton(serviceProvider =>
            Plugin.Instance?.Secrets
            ?? throw new InvalidOperationException("MediaForge secret store is unavailable."));
    }
}
