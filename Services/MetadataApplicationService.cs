using Jellyfin.Plugin.JellyCAP.Configuration;
using Jellyfin.Plugin.JellyCAP.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyCAP.Services;

/// <summary>
/// Applies approved proposal fields to Jellyfin collection metadata.
/// </summary>
public sealed class MetadataApplicationService
{
    private const int BulkBatchSize = 50;

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

            var intended = new MetadataSnapshot
            {
                SortTitle = current.SortTitle,
                ContentRating = current.ContentRating,
                CommunityRating = current.CommunityRating,
                ProductionYear = current.ProductionYear,
                PremiereDate = current.PremiereDate,
                Overview = current.Overview,
                Genres = current.Genres,
                Tags = current.Tags
            };

            if (request.ApplySortTitle)
            {
                intended.SortTitle = NormalizeScalar(request.SortTitle, 500);
            }

            if (request.ApplyContentRating)
            {
                var contentRating = NormalizeScalar(request.ContentRating, 100);
                intended.ContentRating = contentRating;
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

                intended.CommunityRating = request.CommunityRating.HasValue
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

                intended.ProductionYear = request.ProductionYear;
            }

            if (request.ApplyPremiereDate)
            {
                intended.PremiereDate = request.PremiereDate.HasValue
                    ? DateTime.SpecifyKind(request.PremiereDate.Value.Date, DateTimeKind.Utc)
                    : null;
            }

            if (request.ApplyOverview)
            {
                intended.Overview = NormalizeOverview(request.Overview, configuration.MaximumOverviewCharacters);
            }

            if (request.ApplyGenres)
            {
                intended.Genres = NormalizeList(request.Genres, 500);
            }

            if (request.ApplyTags)
            {
                intended.Tags = NormalizeList(request.Tags, 500);
            }

            var intendedMetadata = ToGeneratedMetadata(intended, proposal.Proposed.Warnings);
            await _proposalStore.BeginApplyAsync(proposal, intendedMetadata, cancellationToken).ConfigureAwait(false);

            if (request.ApplySortTitle)
            {
                collection.ForcedSortName = intended.SortTitle;
            }

            if (request.ApplyContentRating)
            {
                collection.OfficialRating = string.IsNullOrWhiteSpace(intended.ContentRating)
                    ? null
                    : intended.ContentRating;
            }

            if (request.ApplyCommunityRating)
            {
                collection.CommunityRating = intended.CommunityRating;
            }

            if (request.ApplyProductionYear)
            {
                collection.ProductionYear = intended.ProductionYear;
            }

            if (request.ApplyPremiereDate)
            {
                collection.PremiereDate = intended.PremiereDate;
            }

            if (request.ApplyOverview)
            {
                collection.Overview = intended.Overview;
            }

            if (request.ApplyGenres)
            {
                collection.Genres = intended.Genres;
            }

            if (request.ApplyTags)
            {
                collection.Tags = intended.Tags;
            }

            collection.OnMetadataChanged();
            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            var applied = MetadataFingerprint.Snapshot(collection);
            proposal.Proposed = ToGeneratedMetadata(applied, proposal.Proposed.Warnings);
            proposal.Status = ProposalStatus.Applied;
            proposal.DecidedAtUtc = DateTimeOffset.UtcNow;
            // The Jellyfin write is irreversible. Persist its outcome even if the HTTP request was cancelled.
            try
            {
                await _proposalStore.SaveAppliedAsync(proposal, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogCritical(
                    exception,
                    "Jellyfin metadata was updated for collection {CollectionId}, but JellyCAP could not finalize proposal {ProposalId}. The durable apply intent was retained for recovery.",
                    proposal.CollectionId,
                    proposal.Id);
                throw;
            }

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
            var rejectedCount = 0;
            DateTimeOffset? beforeCreatedAtUtc = null;
            Guid? beforeId = null;
            while (true)
            {
                var proposals = await _proposalStore.ListPendingBatchAsync(
                    beforeCreatedAtUtc,
                    beforeId,
                    BulkBatchSize,
                    cancellationToken).ConfigureAwait(false);
                if (proposals.Length == 0)
                {
                    break;
                }

                var last = proposals[^1];
                beforeCreatedAtUtc = last.CreatedAtUtc;
                beforeId = last.Id;
                rejectedCount += await _proposalStore.RejectPendingBatchAsync(
                    proposals,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                if (proposals.Length < BulkBatchSize)
                {
                    break;
                }
            }

            return rejectedCount;
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
        var configuration = SettingsValidator.Validate(
            Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("JellyCAP is not initialized."));
        var failures = new List<BulkProposalApprovalFailure>();
        var failedCount = 0;
        var appliedCount = 0;
        DateTimeOffset? beforeCreatedAtUtc = null;
        Guid? beforeId = null;
        while (true)
        {
            var proposals = await _proposalStore.ListPendingBatchAsync(
                beforeCreatedAtUtc,
                beforeId,
                BulkBatchSize,
                cancellationToken).ConfigureAwait(false);
            if (proposals.Length == 0)
            {
                break;
            }

            var last = proposals[^1];
            beforeCreatedAtUtc = last.CreatedAtUtc;
            beforeId = last.Id;
            foreach (var proposal in proposals)
            {
                try
                {
                    await ApplyAsync(
                        proposal.Id,
                        CreateApplyGeneratedRequest(proposal, configuration),
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
                    var errorReference = Guid.NewGuid().ToString("N");
                    _logger.LogError(
                        exception,
                        "JellyCAP proposal {ProposalId} could not be applied during bulk approval for collection {CollectionName} ({CollectionId}). Error reference {ErrorReference}.",
                        proposal.Id,
                        proposal.CollectionName,
                        proposal.CollectionId,
                        errorReference);
                    if (failures.Count < 25)
                    {
                        failures.Add(new BulkProposalApprovalFailure
                        {
                            ProposalId = proposal.Id,
                            CollectionName = proposal.CollectionName,
                            Message = GetSafeBulkFailureMessage(exception, errorReference)
                        });
                    }
                }
            }

            if (proposals.Length < BulkBatchSize)
            {
                break;
            }
        }

        return new BulkProposalApprovalResponse
        {
            AppliedCount = appliedCount,
            FailedCount = failedCount,
            Failures = failures
        };
    }

    /// <summary>
    /// Reconciles durable apply intents left by a process interruption or post-write storage failure.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of proposals finalized as applied.</returns>
    public async Task<int> RecoverInterruptedAppliesAsync(CancellationToken cancellationToken)
    {
        await _decisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var recoveredCount = 0;
            while (true)
            {
                var intents = await _proposalStore.ListApplyIntentsAsync(
                    BulkBatchSize,
                    cancellationToken).ConfigureAwait(false);
                if (intents.Length == 0)
                {
                    break;
                }

                foreach (var intent in intents)
                {
                    var proposal = await _proposalStore.GetAsync(intent.ProposalId, cancellationToken).ConfigureAwait(false);
                    if (proposal is null || proposal.Status != ProposalStatus.Pending)
                    {
                        await _proposalStore.DeleteApplyIntentAsync(intent.ProposalId, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        var collection = _collectionContextService.GetCollection(intent.CollectionId);
                        var current = MetadataFingerprint.Snapshot(collection);
                        var intended = ToMetadataSnapshot(intent.Intended);
                        if (string.Equals(
                            MetadataFingerprint.Compute(current),
                            MetadataFingerprint.Compute(intended),
                            StringComparison.Ordinal))
                        {
                            proposal.Proposed = intent.Intended;
                            proposal.Status = ProposalStatus.Applied;
                            proposal.DecidedAtUtc = intent.StartedAtUtc;
                            await _proposalStore.SaveAppliedAsync(proposal, CancellationToken.None).ConfigureAwait(false);
                            recoveredCount++;
                            _logger.LogInformation(
                                "Recovered interrupted JellyCAP application for proposal {ProposalId} and collection {CollectionId}.",
                                proposal.Id,
                                proposal.CollectionId);
                        }
                        else
                        {
                            await _proposalStore.DeleteApplyIntentAsync(intent.ProposalId, cancellationToken).ConfigureAwait(false);
                            _logger.LogWarning(
                                "Cleared interrupted JellyCAP application intent for proposal {ProposalId}; collection metadata did not match the intended result, so the proposal remains pending.",
                                proposal.Id);
                        }
                    }
                    catch (KeyNotFoundException)
                    {
                        await _proposalStore.DeleteApplyIntentAsync(intent.ProposalId, cancellationToken).ConfigureAwait(false);
                        _logger.LogWarning(
                            "Cleared interrupted JellyCAP application intent for proposal {ProposalId} because collection {CollectionId} no longer exists.",
                            proposal.Id,
                            intent.CollectionId);
                    }
                }
            }

            return recoveredCount;
        }
        finally
        {
            _decisionGate.Release();
        }
    }

    private static ApplyProposalRequest CreateApplyGeneratedRequest(
        MetadataProposal proposal,
        PluginConfiguration configuration)
    {
        return new ApplyProposalRequest
        {
            ApplySortTitle = configuration.GenerateSortTitle
                && !string.Equals(proposal.Original.SortTitle, proposal.Proposed.SortTitle, StringComparison.Ordinal),
            ApplyContentRating = configuration.GenerateContentRating
                && !string.Equals(proposal.Original.ContentRating, proposal.Proposed.ContentRating, StringComparison.Ordinal),
            ApplyCommunityRating = configuration.GenerateCommunityRating
                && proposal.Original.CommunityRating != proposal.Proposed.CommunityRating,
            ApplyProductionYear = configuration.CalculateReleaseMetadata
                && proposal.Original.ProductionYear != proposal.Proposed.ProductionYear,
            ApplyPremiereDate = configuration.CalculateReleaseMetadata
                && proposal.Original.PremiereDate?.Date != proposal.Proposed.PremiereDate?.Date,
            ApplyOverview = configuration.GenerateOverview
                && !string.Equals(proposal.Original.Overview, proposal.Proposed.Overview, StringComparison.Ordinal),
            ApplyGenres = configuration.GenerateGenres
                && !proposal.Original.Genres.SequenceEqual(proposal.Proposed.Genres, StringComparer.Ordinal),
            ApplyTags = configuration.GenerateTags
                && !proposal.Original.Tags.SequenceEqual(proposal.Proposed.Tags, StringComparer.Ordinal),
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

    private static string GetSafeBulkFailureMessage(Exception exception, string errorReference)
    {
        return exception switch
        {
            StaleProposalException => "The collection metadata changed after this proposal was generated.",
            KeyNotFoundException => "The proposal or its collection no longer exists.",
            ArgumentException => "The proposal contains a value that cannot be applied.",
            _ => $"The proposal could not be applied. See the Jellyfin log with error reference {errorReference}."
        };
    }

    private static GeneratedMetadata ToGeneratedMetadata(
        MetadataSnapshot snapshot,
        string[] warnings)
    {
        return new GeneratedMetadata
        {
            SortTitle = snapshot.SortTitle,
            ContentRating = snapshot.ContentRating,
            CommunityRating = snapshot.CommunityRating,
            ProductionYear = snapshot.ProductionYear,
            PremiereDate = snapshot.PremiereDate,
            Overview = snapshot.Overview,
            Genres = snapshot.Genres,
            Tags = snapshot.Tags,
            Warnings = warnings
        };
    }

    private static MetadataSnapshot ToMetadataSnapshot(GeneratedMetadata metadata)
    {
        return new MetadataSnapshot
        {
            SortTitle = metadata.SortTitle,
            ContentRating = metadata.ContentRating,
            CommunityRating = metadata.CommunityRating,
            ProductionYear = metadata.ProductionYear,
            PremiereDate = metadata.PremiereDate,
            Overview = metadata.Overview,
            Genres = metadata.Genres,
            Tags = metadata.Tags
        };
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
