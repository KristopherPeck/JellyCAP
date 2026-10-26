using System.Globalization;
using Jellyfin.Plugin.JellyAnnotator.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyAnnotator;

/// <summary>
/// JellyAnnotator plugin entry point.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The stable plugin identifier.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("4f8b7813-21dd-49f6-9b60-cbcd31fbac61");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">Jellyfin XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the active plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "JellyAnnotator";

    /// <inheritdoc />
    public override string Description => "Generate and review collection overviews, genres, tags, sort titles, and content ratings with an AI provider.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "JellyAnnotator",
                DisplayName = "JellyAnnotator",
                MenuIcon = "auto_awesome",
                EnableInMainMenu = true,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.dashboard.html",
                    GetType().Namespace)
            }
        ];
    }
}

