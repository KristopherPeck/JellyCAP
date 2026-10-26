using Jellyfin.Plugin.JellyCAP.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyCAP;

/// <summary>
/// Registers JellyCAP services in Jellyfin's dependency injection container.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(
        IServiceCollection serviceCollection,
        IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SecretStore>();
        serviceCollection.AddSingleton<ProposalStore>();
        serviceCollection.AddSingleton<CollectionContextService>();
        serviceCollection.AddSingleton<AiProviderClient>();
        serviceCollection.AddSingleton<ProposalService>();
        serviceCollection.AddSingleton<MetadataApplicationService>();
    }
}
