using Jellyfin.Plugin.JellyAnnotator.Configuration;

namespace Jellyfin.Plugin.JellyAnnotator.Models;

/// <summary>
/// Settings returned to the administration page.
/// </summary>
public sealed class SettingsResponse
{
    /// <summary>
    /// Gets or sets the non-secret configuration.
    /// </summary>
    public PluginConfiguration Configuration { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether a usable API key is configured.
    /// </summary>
    public bool HasApiKey { get; set; }

    /// <summary>
    /// Gets or sets the source of the effective key: environment, stored, or none.
    /// </summary>
    public string ApiKeySource { get; set; } = "none";
}

/// <summary>
/// Settings update submitted by the administration page.
/// </summary>
public sealed class SettingsUpdateRequest
{
    /// <summary>
    /// Gets or sets the non-secret configuration.
    /// </summary>
    public PluginConfiguration Configuration { get; set; } = new();

    /// <summary>
    /// Gets or sets a replacement API key. Null or blank preserves the stored key.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the stored key should be removed.
    /// </summary>
    public bool ClearStoredApiKey { get; set; }
}

/// <summary>
/// Result of a provider connection test.
/// </summary>
public sealed class ConnectionTestResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the request succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a human-readable result.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

