namespace Jellyfin.Plugin.JellyCAP.Models;

/// <summary>
/// A collection displayed in the workbench.
/// </summary>
public sealed class CollectionSummary
{
    /// <summary>
    /// Gets or sets the collection identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the collection name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current sort title.
    /// </summary>
    public string SortTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current content rating.
    /// </summary>
    public string ContentRating { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current genres.
    /// </summary>
    public string[] Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets the current tags.
    /// </summary>
    public string[] Tags { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of direct linked entries.
    /// </summary>
    public int LinkedItemCount { get; set; }
}

/// <summary>
/// Sanitized metadata for a collection member sent to the provider.
/// </summary>
public sealed class CollectionContextItem
{
    /// <summary>
    /// Gets or sets the Jellyfin item kind.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item sort title.
    /// </summary>
    public string SortTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item content rating.
    /// </summary>
    public string ContentRating { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the production year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the item overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item genres.
    /// </summary>
    public string[] Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets the item tags.
    /// </summary>
    public string[] Tags { get; set; } = [];
}

/// <summary>
/// Collection metadata and the bounded content context used for generation.
/// </summary>
public sealed class CollectionContext
{
    /// <summary>
    /// Gets or sets the collection name.
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection's current sort title.
    /// </summary>
    public string CurrentSortTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection's current content rating.
    /// </summary>
    public string CurrentContentRating { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection's current overview.
    /// </summary>
    public string CurrentOverview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection's current genres.
    /// </summary>
    public string[] CurrentGenres { get; set; } = [];

    /// <summary>
    /// Gets or sets the collection's current tags.
    /// </summary>
    public string[] CurrentTags { get; set; } = [];

    /// <summary>
    /// Gets or sets the selected child metadata.
    /// </summary>
    public CollectionContextItem[] Items { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of eligible items omitted by the configured context limit.
    /// </summary>
    public int OmittedItemCount { get; set; }
}

