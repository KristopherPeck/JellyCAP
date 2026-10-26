namespace Jellyfin.Plugin.JellyCAP.Models;

/// <summary>
/// Filters collections by durable JellyCAP activity.
/// </summary>
public enum CollectionTrackingFilter
{
    /// <summary>Returns every collection.</summary>
    All,

    /// <summary>Returns collections without a successful generation.</summary>
    NeverGenerated,

    /// <summary>Returns collections with at least one successful generation.</summary>
    Generated,

    /// <summary>Returns collections currently waiting in the server queue.</summary>
    Queued,

    /// <summary>Returns collections currently being generated.</summary>
    Generating,

    /// <summary>Returns collections whose most recent completed attempt failed.</summary>
    Failed,

    /// <summary>Returns collections with metadata previously applied by JellyCAP.</summary>
    Applied,

    /// <summary>Returns collections that currently have at least one pending proposal.</summary>
    PendingReview
}

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
    /// Gets or sets the current community rating.
    /// </summary>
    public float? CommunityRating { get; set; }

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

    /// <summary>
    /// Gets or sets durable generation and review activity for this collection.
    /// </summary>
    public CollectionGenerationActivity Activity { get; set; } = new();
}

/// <summary>
/// Durable generation summary retained independently of proposal history.
/// </summary>
public sealed class CollectionGenerationActivity
{
    /// <summary>Gets or sets the collection identifier.</summary>
    public Guid CollectionId { get; set; }

    /// <summary>Gets or sets the total number of successfully generated proposals.</summary>
    public long GenerationCount { get; set; }

    /// <summary>Gets or sets the most recent generation-attempt timestamp.</summary>
    public DateTimeOffset? LastAttemptedAtUtc { get; set; }

    /// <summary>Gets or sets the most recent successful-generation timestamp.</summary>
    public DateTimeOffset? LastGeneratedAtUtc { get; set; }

    /// <summary>Gets or sets the most recent metadata-application timestamp.</summary>
    public DateTimeOffset? LastAppliedAtUtc { get; set; }

    /// <summary>Gets or sets whether the most recent completed attempt succeeded.</summary>
    public bool? LastGenerationSucceeded { get; set; }

    /// <summary>Gets or sets the bounded error from the most recent failed attempt.</summary>
    public string LastGenerationError { get; set; } = string.Empty;

    /// <summary>Gets or sets the current queued or running job status.</summary>
    public GenerationJobStatus? ActiveJobStatus { get; set; }

    /// <summary>Gets or sets whether at least one proposal is awaiting review.</summary>
    public bool HasPendingReview { get; set; }
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
    /// Gets or sets the item community rating.
    /// </summary>
    public float? CommunityRating { get; set; }

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
    /// Gets or sets the collection's current community rating.
    /// </summary>
    public float? CurrentCommunityRating { get; set; }

    /// <summary>
    /// Gets or sets the average of all eligible child community ratings.
    /// </summary>
    public float? AverageCommunityRating { get; set; }

    /// <summary>
    /// Gets or sets the number of eligible child ratings used in the average.
    /// </summary>
    public int CommunityRatingItemCount { get; set; }

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

