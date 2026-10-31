namespace Jellyfin.Plugin.JellyCAP.Models;

/// <summary>
/// Lifecycle state for a generated proposal.
/// </summary>
public enum ProposalStatus
{
    /// <summary>
    /// The proposal is waiting for an administrator decision.
    /// </summary>
    Pending,

    /// <summary>
    /// The selected fields have been applied.
    /// </summary>
    Applied,

    /// <summary>
    /// The proposal was rejected.
    /// </summary>
    Rejected,

    /// <summary>
    /// The generated values were semantically identical to the collection's current metadata.
    /// </summary>
    Unchanged
}

/// <summary>
/// Controls proposal list ordering.
/// </summary>
public enum ProposalSortOrder
{
    /// <summary>Returns the newest proposals first.</summary>
    Newest,

    /// <summary>Returns collection names from A to Z.</summary>
    NameAscending,

    /// <summary>Returns collection names from Z to A.</summary>
    NameDescending
}

/// <summary>
/// The metadata fields returned by an AI provider.
/// </summary>
public sealed class GeneratedMetadata
{
    /// <summary>
    /// Gets or sets the generated sort title.
    /// </summary>
    public string SortTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the generated content rating.
    /// </summary>
    public string ContentRating { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the generated community rating.
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    /// Gets or sets the locally calculated production year.
    /// </summary>
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets the locally calculated premiere or release date.
    /// </summary>
    public DateTime? PremiereDate { get; set; }

    /// <summary>
    /// Gets or sets the generated overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the generated genres.
    /// </summary>
    public string[] Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets the generated tags.
    /// </summary>
    public string[] Tags { get; set; } = [];

    /// <summary>
    /// Gets or sets short provider warnings for the reviewer.
    /// </summary>
    public string[] Warnings { get; set; } = [];
}

/// <summary>
/// A snapshot of the mutable collection metadata.
/// </summary>
public sealed class MetadataSnapshot
{
    /// <summary>
    /// Gets or sets the sort title.
    /// </summary>
    public string SortTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the content rating.
    /// </summary>
    public string ContentRating { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the community rating.
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    /// Gets or sets the production year.
    /// </summary>
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets the premiere or release date.
    /// </summary>
    public DateTime? PremiereDate { get; set; }

    /// <summary>
    /// Gets or sets the overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the genres.
    /// </summary>
    public string[] Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets the tags.
    /// </summary>
    public string[] Tags { get; set; } = [];
}

/// <summary>
/// A generated, reviewable collection metadata proposal.
/// </summary>
public sealed class MetadataProposal
{
    /// <summary>
    /// Gets or sets the proposal identifier.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the collection identifier.
    /// </summary>
    public Guid CollectionId { get; set; }

    /// <summary>
    /// Gets or sets the collection name at generation time.
    /// </summary>
    public string CollectionName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original collection metadata.
    /// </summary>
    public MetadataSnapshot Original { get; set; } = new();

    /// <summary>
    /// Gets or sets a fingerprint used to detect metadata changed after generation.
    /// </summary>
    public string OriginalFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the metadata fingerprint format version.
    /// </summary>
    public int FingerprintVersion { get; set; }

    /// <summary>
    /// Gets or sets the proposed final metadata after merge policies are applied.
    /// </summary>
    public GeneratedMetadata Proposed { get; set; } = new();

    /// <summary>
    /// Gets or sets the provider protocol used to create the proposal.
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model identifier used to create the proposal.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of collection members included in the request.
    /// </summary>
    public int ContextItemCount { get; set; }

    /// <summary>
    /// Gets or sets how the proposed community rating was produced.
    /// </summary>
    public string CommunityRatingSource { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how the proposed production year and premiere date were produced.
    /// </summary>
    public string ReleaseMetadataSource { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the proposal status.
    /// </summary>
    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;

    /// <summary>
    /// Gets or sets the creation timestamp.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the application or rejection timestamp.
    /// </summary>
    public DateTimeOffset? DecidedAtUtc { get; set; }
}

/// <summary>
/// Reviewer's selected and optionally edited values when applying a proposal.
/// </summary>
public sealed class ApplyProposalRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether the sort title should be applied.
    /// </summary>
    public bool ApplySortTitle { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the content rating should be applied.
    /// </summary>
    public bool ApplyContentRating { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the community rating should be applied.
    /// </summary>
    public bool ApplyCommunityRating { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the production year should be applied.
    /// </summary>
    public bool ApplyProductionYear { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the premiere or release date should be applied.
    /// </summary>
    public bool ApplyPremiereDate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the overview should be applied.
    /// </summary>
    public bool ApplyOverview { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether genres should be applied.
    /// </summary>
    public bool ApplyGenres { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether tags should be applied.
    /// </summary>
    public bool ApplyTags { get; set; }

    /// <summary>
    /// Gets or sets the reviewed sort title.
    /// </summary>
    public string SortTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the reviewed content rating.
    /// </summary>
    public string ContentRating { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the reviewed community rating.
    /// </summary>
    public float? CommunityRating { get; set; }

    /// <summary>
    /// Gets or sets the reviewed production year.
    /// </summary>
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets the reviewed premiere or release date.
    /// </summary>
    public DateTime? PremiereDate { get; set; }

    /// <summary>
    /// Gets or sets the reviewed overview.
    /// </summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the reviewed genres.
    /// </summary>
    public string[] Genres { get; set; } = [];

    /// <summary>
    /// Gets or sets the reviewed tags.
    /// </summary>
    public string[] Tags { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether a stale proposal may overwrite metadata changed since generation.
    /// </summary>
    public bool Force { get; set; }
}

