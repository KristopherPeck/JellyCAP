using Jellyfin.Plugin.JellyCAP.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyCAP.Services;

/// <summary>
/// Applies approved proposal fields to Jellyfin collection metadata.
/// </summary>
public sealed class MetadataApplicationService
{
    private readonly SemaphoreSlim _decisionGate = new(1, 1);
    private readonly CollectionContextService _collectionContextService;
    private readonly ProposalStore _proposalStore;
    private readonly ILogger<MetadataApplicationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataApplicationService"/> class.
    /// </summary>
    public MetadataApplicationService(
        CollectionContextService collectionContextService,
        ProposalStore proposalStore,
        ILogger<MetadataApplicationService> logger)
    {
        _collectionContextService = collectionContextService;
        _proposalStore = proposalStore;
        _logger = logger;
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
        await _decisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!request.ApplyOverview
                && !request.ApplyGenres
                && !request.ApplyTags
                && !request.ApplySortTitle
                && !request.ApplyContentRating
                && !request.ApplyCommunityRating
                && !request.ApplyProductionYear
                && !request.ApplyPremiereDate)
            {
                throw new ArgumentException("Select at least one metadata field to apply.", nameof(request));
            }

            var proposal = await GetPendingAsync(proposalId, cancellationToken).ConfigureAwait(false);
            var collection = _collectionContextService.GetCollection(proposal.CollectionId);
            var current = MetadataFingerprint.Snapshot(collection);
            var currentFingerprint = proposal.FingerprintVersion switch
            {
                >= MetadataFingerprint.CurrentVersion => MetadataFingerprint.Compute(current),
                2 => MetadataFingerprint.ComputeVersion2(current),
                _ => MetadataFingerprint.ComputeLegacy(current)
            };
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

            if (request.ApplyCommunityRating)
            {
                if (request.CommunityRating is < 0 or > 10
                    || (request.CommunityRating.HasValue && !float.IsFinite(request.CommunityRating.Value)))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(request),
                        "Community rating must be between 0 and 10.");
                }

                collection.CommunityRating = request.CommunityRating.HasValue
                    ? (float)Math.Round(request.CommunityRating.Value, 1, MidpointRounding.AwayFromZero)
                    : null;
            }

            if (request.ApplyProductionYear)
            {
                if (request.ProductionYear is < 1 or > 9999)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(request),
                        "Production year must be between 1 and 9999.");
                }

                collection.ProductionYear = request.ProductionYear;
            }

            if (request.ApplyPremiereDate)
            {
                collection.PremiereDate = request.PremiereDate.HasValue
                    ? DateTime.SpecifyKind(request.PremiereDate.Value.Date, DateTimeKind.Utc)
                    : null;
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
                CommunityRating = applied.CommunityRating,
                ProductionYear = applied.ProductionYear,
                PremiereDate = applied.PremiereDate,
                Overview = applied.Overview,
                Genres = applied.Genres,
                Tags = applied.Tags,
                Warnings = proposal.Proposed.Warnings
            };
            proposal.Status = ProposalStatus.Applied;
            proposal.DecidedAtUtc = DateTimeOffset.UtcNow;
            await _proposalStore.SaveAppliedAsync(proposal, cancellationToken).ConfigureAwait(false);
            return proposal;
        }
        finally
        {
            _decisionGate.Release();
        }
    }

    /// <summary>
    /// Rejects a pending proposal without changing Jellyfin metadata.
    /// </summary>
    /// <param name="proposalId">Proposal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rejected proposal.</returns>
    public async Task<MetadataProposal> RejectAsync(Guid proposalId, CancellationToken cancellationToken)
    {
        await _decisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var proposal = await GetPendingAsync(proposalId, cancellationToken).ConfigureAwait(false);
            proposal.Status = ProposalStatus.Rejected;
            proposal.DecidedAtUtc = DateTimeOffset.UtcNow;
            await _proposalStore.SaveAsync(proposal, cancellationToken).ConfigureAwait(false);
            return proposal;
        }
        finally
        {
            _decisionGate.Release();
        }
    }

    /// <summary>
    /// Rejects every pending proposal without changing Jellyfin metadata.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of proposals rejected.</returns>
    public async Task<int> RejectAllPendingAsync(CancellationToken cancellationToken)
    {
        await _decisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _proposalStore.RejectAllPendingAsync(
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _decisionGate.Release();
        }
    }

    /// <summary>
    /// Applies the generated changes from every proposal currently pending.
    /// Stale or invalid proposals remain pending and do not stop the remaining approvals.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Counts and bounded failure details for the bulk operation.</returns>
    public async Task<BulkProposalApprovalResponse> ApplyAllPendingAsync(CancellationToken cancellationToken)
    {
        var proposals = await _proposalStore.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        var failures = new List<BulkProposalApprovalFailure>();
        var failedCount = 0;
        var appliedCount = 0;
        foreach (var proposal in proposals)
        {
            try
            {
                await ApplyAsync(
                    proposal.Id,
                    CreateApplyGeneratedRequest(proposal),
                    cancellationToken).ConfigureAwait(false);
                appliedCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failedCount++;
                _logger.LogError(
                    exception,
                    "JellyCAP proposal {ProposalId} could not be applied during bulk approval for collection {CollectionName} ({CollectionId}).",
                    proposal.Id,
                    proposal.CollectionName,
                    proposal.CollectionId);
                if (failures.Count < 25)
                {
                    failures.Add(new BulkProposalApprovalFailure
                    {
                        ProposalId = proposal.Id,
                        CollectionName = proposal.CollectionName,
                        Message = BoundFailureMessage(exception.Message)
                    });
                }
            }
        }

        return new BulkProposalApprovalResponse
        {
            AppliedCount = appliedCount,
            FailedCount = failedCount,
            Failures = failures
        };
    }

    private static ApplyProposalRequest CreateApplyGeneratedRequest(MetadataProposal proposal)
    {
        return new ApplyProposalRequest
        {
            ApplySortTitle = !string.Equals(proposal.Original.SortTitle, proposal.Proposed.SortTitle, StringComparison.Ordinal),
            ApplyContentRating = !string.Equals(proposal.Original.ContentRating, proposal.Proposed.ContentRating, StringComparison.Ordinal),
            ApplyCommunityRating = proposal.Original.CommunityRating != proposal.Proposed.CommunityRating,
            ApplyProductionYear = proposal.Original.ProductionYear != proposal.Proposed.ProductionYear,
            ApplyPremiereDate = proposal.Original.PremiereDate?.Date != proposal.Proposed.PremiereDate?.Date,
            ApplyOverview = !string.Equals(proposal.Original.Overview, proposal.Proposed.Overview, StringComparison.Ordinal),
            ApplyGenres = !proposal.Original.Genres.SequenceEqual(proposal.Proposed.Genres, StringComparer.Ordinal),
            ApplyTags = !proposal.Original.Tags.SequenceEqual(proposal.Proposed.Tags, StringComparer.Ordinal),
            SortTitle = proposal.Proposed.SortTitle,
            ContentRating = proposal.Proposed.ContentRating,
            CommunityRating = proposal.Proposed.CommunityRating,
            ProductionYear = proposal.Proposed.ProductionYear,
            PremiereDate = proposal.Proposed.PremiereDate,
            Overview = proposal.Proposed.Overview,
            Genres = proposal.Proposed.Genres,
            Tags = proposal.Proposed.Tags
        };
    }

    private static string BoundFailureMessage(string? message)
    {
        var normalized = message?.Trim() ?? "The proposal could not be applied.";
        return normalized.Length <= 500 ? normalized : normalized[..500].TrimEnd();
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
            .Select(value => value.Length <= 200 ? value : value[..200].TrimEnd())
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
