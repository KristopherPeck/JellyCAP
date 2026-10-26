namespace Jellyfin.Plugin.JellyAnnotator.Configuration;

/// <summary>
/// Determines how generated list metadata is combined with existing values.
/// </summary>
public enum ListUpdateMode
{
    /// <summary>
    /// Preserve existing values and append generated values.
    /// </summary>
    Merge,

    /// <summary>
    /// Replace existing values with generated values.
    /// </summary>
    Replace,

    /// <summary>
    /// Generate a value only when the existing list is empty.
    /// </summary>
    OnlyIfEmpty
}

