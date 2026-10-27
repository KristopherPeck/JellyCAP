using System.Globalization;
using Jellyfin.Plugin.JellyCAP.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyCAP;

/// <summary>
/// JellyCAP plugin entry point.
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
    public override string Name => "JellyCAP";

    /// <inheritdoc />
    public override string ConfigurationFileName => "Jellyfin.Plugin.JellyCAP.xml";

    /// <inheritdoc />
    public override string Description => "Jellyfin Collection Annotation Plugin: generate and review collection overviews, genres, tags, sort titles, content ratings, and community ratings.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// Resolves a file in the JellyCAP plugin data directory.
    /// </summary>
    /// <param name="fileName">Plugin-owned file name.</param>
    /// <returns>The active file path.</returns>
    public string GetDataFilePath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.IsPathRooted(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName is "." or "..")
        {
            throw new ArgumentException("Plugin data files must use a simple file name.", nameof(fileName));
        }

        return Path.Combine(DataFolderPath, fileName);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "JellyCAP",
                DisplayName = "JellyCAP",
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

