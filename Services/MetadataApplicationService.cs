using Jellyfin.Plugin.JellyCAP.Models;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyCAP.Services;

/// <summary>
/// Applies approved proposal fields to Jellyfin collection metadata.
/// </summary>
public sealed class MetadataApplicationService
{
    private readonly CollectionContextService _collectionContextService;
    private readonly ProposalStore _proposalStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataApplicationService"/> class.
    /// </summary>
    public MetadataApplicationService(
        CollectionContextService collectionContextService,
        ProposalStore proposalStore)
    {
        _collectionContextService = collectionContextService;
        _proposalStore = proposalStore;
    }

    /// <summary>
    /// Applies the reviewer-selected fields after checking that the collection has not changed.
    /// </summary>
    /// <param name="proposalId">Proposal identifier.</param>
    /// <param name="request">Reviewed values and field selection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decided proposal.</returns>
    public async Task<MetadataProposal> ApplyAsync(
        Guid proposalId,
        ApplyProposalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.ApplyOverview
            && !request.ApplyGenres
            && !request.ApplyTags
            && !request.ApplySortTitle
            && !request.ApplyContentRating)
        {
            throw new ArgumentException("Select at least one metadata field to apply.", nameof(request));
        }

        var proposal = await GetPendingAsync(proposalId, cancellationToken).ConfigureAwait(false);
        var collection = _collectionContextService.GetCollection(proposal.CollectionId);
        var current = MetadataFingerprint.Snapshot(collection);
        var currentFingerprint = MetadataFingerprint.Compute(current);
        if (!request.Force
            && !string.Equals(currentFingerprint, proposal.OriginalFingerprint, StringComparison.Ordinal))
        {
            throw new StaleProposalException(
                "The collection metadata changed after this proposal was generated. Review the current metadata or explicitly force the update.");
        }

        var configuration = SettingsValidator.Validate(
            Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("JellyCAP is not initialized."));

        if (request.ApplySortTitle)
        {
            collection.ForcedSortName = NormalizeScalar(request.SortTitle, 500);
        }

        if (request.ApplyContentRating)
        {
            var contentRating = NormalizeScalar(request.ContentRating, 100);
            collection.OfficialRating = string.IsNullOrWhiteSpace(contentRating) ? null : contentRating;
        }

        if (request.ApplyOverview)
        {
            collection.Overview = NormalizeOverview(request.Overview, configuration.MaximumOverviewCharacters);
        }

        if (request.ApplyGenres)
        {
            collection.Genres = NormalizeList(request.Genres, 500);
        }

        if (request.ApplyTags)
        {
            collection.Tags = NormalizeList(request.Tags, 500);
        }

        collection.OnMetadataChanged();
        await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

        var applied = MetadataFingerprint.Snapshot(collection);
        proposal.Proposed = new GeneratedMetadata
        {
            SortTitle = applied.SortTitle,
            ContentRating = applied.ContentRating,
            Overview = applied.Overview,
            Genres = applied.Genres,
            Tags = applied.Tags,
            Warnings = proposal.Proposed.Warnings
        };
        proposal.Status = ProposalStatus.Applied;
        proposal.DecidedAtUtc = DateTimeOffset.UtcNow;
        await _proposalStore.SaveAsync(proposal, cancellationToken).ConfigureAwait(false);
        return proposal;
    }

    /// <summary>
    /// Rejects a pending proposal without changing Jellyfin metadata.
    /// </summary>
    /// <param name="proposalId">Proposal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rejected proposal.</returns>
    public async Task<MetadataProposal> RejectAsync(Guid proposalId, CancellationToken cancellationToken)
    {
        var proposal = await GetPendingAsync(proposalId, cancellationToken).ConfigureAwait(false);
        proposal.Status = ProposalStatus.Rejected;
        proposal.DecidedAtUtc = DateTimeOffset.UtcNow;
        await _proposalStore.SaveAsync(proposal, cancellationToken).ConfigureAwait(false);
        return proposal;
    }

    private async Task<MetadataProposal> GetPendingAsync(Guid proposalId, CancellationToken cancellationToken)
    {
        var proposal = await _proposalStore.GetAsync(proposalId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Proposal '{proposalId}' was not found.");
        if (proposal.Status != ProposalStatus.Pending)
        {
            throw new InvalidOperationException("Only pending proposals can be decided.");
        }

        return proposal;
    }

    private static string NormalizeOverview(string? value, int maximumCharacters)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters].TrimEnd();
    }

    private static string NormalizeScalar(string? value, int maximumCharacters)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters].TrimEnd();
    }

    private static string[] NormalizeList(IEnumerable<string>? values, int maximumItems)
    {
        return (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maximumItems)
            .ToArray();
    }
}

/// <summary>
/// Indicates that a collection changed after its proposal was generated.
/// </summary>
public sealed class StaleProposalException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StaleProposalException"/> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    public StaleProposalException(string message)
        : base(message)
    {
    }
}
