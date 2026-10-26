namespace Jellyfin.Plugin.JellyAnnotator.Configuration;

/// <summary>
/// Determines when a generated overview is proposed.
/// </summary>
public enum OverviewUpdateMode
{
    /// <summary>
    /// Replace the current overview in the proposal.
    /// </summary>
    Replace,

    /// <summary>
    /// Generate an overview only when the collection has no overview.
    /// </summary>
    OnlyIfEmpty
}

