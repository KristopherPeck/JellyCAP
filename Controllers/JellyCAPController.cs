using Jellyfin.Plugin.JellyCAP.Models;
using Jellyfin.Plugin.JellyCAP.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyCAP.Controllers;

/// <summary>
/// Administrative API used by the embedded JellyCAP workbench.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("JellyCAP")]
public sealed class JellyCAPController : ControllerBase
{
    private readonly CollectionContextService _collectionContextService;
    private readonly MetadataApplicationService _metadataApplicationService;
    private readonly ProposalService _proposalService;
    private readonly ProposalStore _proposalStore;
    private readonly GenerationQueueService _generationQueueService;
    private readonly SecretStore _secretStore;
    private readonly ILogger<JellyCAPController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyCAPController"/> class.
    /// </summary>
    public JellyCAPController(
        CollectionContextService collectionContextService,
        MetadataApplicationService metadataApplicationService,
        ProposalService proposalService,
        ProposalStore proposalStore,
        GenerationQueueService generationQueueService,
        SecretStore secretStore,
        ILogger<JellyCAPController> logger)
    {
        _collectionContextService = collectionContextService;
        _metadataApplicationService = metadataApplicationService;
        _proposalService = proposalService;
        _proposalStore = proposalStore;
        _generationQueueService = generationQueueService;
        _secretStore = secretStore;
        _logger = logger;
    }

    /// <summary>
    /// Gets plugin settings without exposing provider credentials.
    /// </summary>
    [HttpGet("Settings")]
    [ProducesResponseType<SettingsResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SettingsResponse>> GetSettings(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("JellyCAP is not initialized.");
        var safeEnvironmentVariable = SettingsValidator.IsAllowedApiKeyEnvironmentVariable(configuration)
            ? configuration.ApiKeyEnvironmentVariable
            : string.Empty;
        var (apiKey, source) = await _secretStore.ResolveAsync(
            safeEnvironmentVariable,
            cancellationToken).ConfigureAwait(false);
        return Ok(new SettingsResponse
        {
            Configuration = configuration.Copy(),
            HasApiKey = !string.IsNullOrWhiteSpace(apiKey),
            ApiKeySource = source
        });
    }

    /// <summary>
    /// Validates and saves plugin settings and, when supplied, a provider credential.
    /// </summary>
    [HttpPut("Settings")]
    [RequestSizeLimit(64 * 1024)]
    [ProducesResponseType<SettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SettingsResponse>> UpdateSettings(
        [FromBody] SettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var configuration = SettingsValidator.Validate(request.Configuration);
            var replacementApiKey = string.IsNullOrWhiteSpace(request.ApiKey)
                ? null
                : SecretStore.ValidateApiKey(request.ApiKey);
            var plugin = Plugin.Instance
                ?? throw new InvalidOperationException("JellyCAP is not initialized.");
            plugin.UpdateConfiguration(configuration);

            if (request.ClearStoredApiKey)
            {
                await _secretStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            }

            if (replacementApiKey is not null)
            {
                await _secretStore.WriteAsync(replacementApiKey, cancellationToken).ConfigureAwait(false);
            }

            await _proposalStore.PruneAsync(cancellationToken).ConfigureAwait(false);

            return await GetSettings(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            _logger.LogWarning(exception, "JellyCAP settings were rejected as invalid.");
            return BadRequest(new ProblemDetails { Title = "Invalid settings", Detail = exception.Message });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Failed to save JellyCAP settings.");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ProblemDetails { Title = "Settings could not be saved", Detail = exception.Message });
        }
    }

    /// <summary>
    /// Sends a small synthetic request to the configured provider.
    /// </summary>
    [HttpPost("Settings/Test")]
    [RequestSizeLimit(1024)]
    [ProducesResponseType<ConnectionTestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ConnectionTestResponse>> TestConnection(CancellationToken cancellationToken)
    {
        try
        {
            await _proposalService.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new ConnectionTestResponse { Success = true, Message = "Provider connection succeeded." });
        }
        catch (GenerationCapacityException exception)
        {
            _logger.LogWarning(exception, "JellyCAP rejected a provider test because the concurrency limit was reached.");
            return StatusCode(
                StatusCodes.Status429TooManyRequests,
                new ConnectionTestResponse { Success = false, Message = exception.Message });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "JellyCAP provider connection test failed.");
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new ConnectionTestResponse { Success = false, Message = exception.Message });
        }
    }

    /// <summary>
    /// Lists Jellyfin collections available for annotation.
    /// </summary>
    [HttpGet("Collections")]
    [ProducesResponseType<PagedResult<CollectionSummary>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<CollectionSummary>>> GetCollections(
        CancellationToken cancellationToken,
        [FromQuery] string? search = null,
        [FromQuery] CollectionTrackingFilter trackingFilter = CollectionTrackingFilter.All,
        [FromQuery] CollectionSortOrder sortOrder = CollectionSortOrder.NameAscending,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50)
    {
        try
        {
            var activity = await _proposalStore.GetCollectionActivityAsync(cancellationToken).ConfigureAwait(false);
            return Ok(_collectionContextService.ListCollections(
                search,
                trackingFilter,
                activity,
                sortOrder,
                startIndex,
                limit));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to list Jellyfin collections for JellyCAP.");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ProblemDetails { Title = "Collections could not be loaded", Detail = exception.Message });
        }
    }

    /// <summary>
    /// Gets durable activity for the collection cards currently visible in the dashboard.
    /// </summary>
    [HttpGet("Collections/Activity")]
    [ProducesResponseType<CollectionGenerationActivity[]>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CollectionGenerationActivity[]>> GetCollectionActivity(
        CancellationToken cancellationToken,
        [FromQuery] Guid[] collectionIds)
    {
        var requestedIds = collectionIds.Distinct().Take(100).ToArray();
        var activity = await _proposalStore.GetCollectionActivityAsync(cancellationToken).ConfigureAwait(false);
        return Ok(requestedIds
            .Select(collectionId => activity.GetValueOrDefault(collectionId)
                ?? new CollectionGenerationActivity { CollectionId = collectionId })
            .ToArray());
    }

    /// <summary>
    /// Shows the exact sanitized collection context that would be sent to a provider.
    /// </summary>
    [HttpGet("Collections/{collectionId:guid}/Context")]
    [ProducesResponseType<CollectionContext>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<CollectionContext> GetCollectionContext(Guid collectionId)
    {
        try
        {
            var maximumItems = Plugin.Instance?.Configuration.MaximumContextItems ?? 200;
            return Ok(_collectionContextService.BuildContext(collectionId, maximumItems));
        }
        catch (KeyNotFoundException exception)
        {
            _logger.LogWarning(exception, "JellyCAP collection {CollectionId} was not found.", collectionId);
            return NotFound(new ProblemDetails { Title = "Collection not found", Detail = exception.Message });
        }
    }

    /// <summary>
    /// Queues a pending metadata proposal for one collection.
    /// </summary>
    [HttpPost("Collections/{collectionId:guid}/Generate")]
    [ProducesResponseType<GenerationEnqueueResult>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<GenerationEnqueueResult>> Generate(
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _generationQueueService.EnqueueAsync(collectionId, cancellationToken).ConfigureAwait(false);
            return Accepted(result);
        }
        catch (KeyNotFoundException exception)
        {
            _logger.LogWarning(exception, "JellyCAP collection {CollectionId} was not found during generation.", collectionId);
            return NotFound(new ProblemDetails { Title = "Collection not found", Detail = exception.Message });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "JellyCAP could not queue collection {CollectionId}.", collectionId);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ProblemDetails { Title = "Generation could not be queued", Detail = exception.Message });
        }
    }

    /// <summary>
    /// Queues all collections matching the supplied dashboard filters.
    /// </summary>
    [HttpPost("Collections/Queue")]
    [ProducesResponseType<GenerationEnqueueResult>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<GenerationEnqueueResult>> QueueCollections(
        CancellationToken cancellationToken,
        [FromQuery] string? search = null,
        [FromQuery] CollectionTrackingFilter trackingFilter = CollectionTrackingFilter.All,
        [FromQuery] bool skipPendingReviews = true)
    {
        try
        {
            var activity = await _proposalStore.GetCollectionActivityAsync(cancellationToken).ConfigureAwait(false);
            var collectionIds = _collectionContextService.ListCollectionIds(search, trackingFilter, activity);
            var eligibleIds = skipPendingReviews
                ? collectionIds.Where(collectionId => activity.GetValueOrDefault(collectionId)?.HasPendingReview != true).ToArray()
                : collectionIds;
            var result = await _generationQueueService.EnqueueAsync(eligibleIds, cancellationToken).ConfigureAwait(false);
            result.SkippedPendingReviewCount = collectionIds.Length - eligibleIds.Length;
            return Accepted(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "JellyCAP could not queue the filtered collection batch.");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new ProblemDetails { Title = "Collections could not be queued", Detail = exception.Message });
        }
    }

    /// <summary>
    /// Cancels a queued generation job for one collection if a worker has not started it.
    /// </summary>
    [HttpPost("Collections/{collectionId:guid}/GenerationQueue/Cancel")]
    [ProducesResponseType<GenerationCancellationResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GenerationCancellationResult>> CancelQueuedCollection(
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        var cancelled = await _proposalStore.CancelQueuedGenerationJobsAsync(
            collectionId,
            cancellationToken).ConfigureAwait(false);
        return Ok(new GenerationCancellationResult { CancelledCount = cancelled });
    }

    /// <summary>
    /// Cancels every queued generation job without interrupting running provider requests.
    /// </summary>
    [HttpPost("GenerationQueue/CancelQueued")]
    [ProducesResponseType<GenerationCancellationResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GenerationCancellationResult>> CancelAllQueued(CancellationToken cancellationToken)
    {
        var cancelled = await _proposalStore.CancelQueuedGenerationJobsAsync(
            null,
            cancellationToken).ConfigureAwait(false);
        return Ok(new GenerationCancellationResult { CancelledCount = cancelled });
    }

    /// <summary>
    /// Gets aggregate durable generation queue state.
    /// </summary>
    [HttpGet("GenerationQueue/Status")]
    [ProducesResponseType<GenerationQueueStatus>(StatusCodes.Status200OK)]
    public async Task<ActionResult<GenerationQueueStatus>> GetGenerationQueueStatus(CancellationToken cancellationToken)
    {
        return Ok(await _proposalStore.GetGenerationQueueStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Lists proposals, optionally filtered by lifecycle status.
    /// </summary>
    [HttpGet("Proposals")]
    [ProducesResponseType<PagedResult<MetadataProposal>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<MetadataProposal>>> GetProposals(
        CancellationToken cancellationToken,
        [FromQuery] ProposalStatus? status = null,
        [FromQuery] ProposalSortOrder sortOrder = ProposalSortOrder.Newest,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 25)
    {
        var proposals = await _proposalStore.ListAsync(status, sortOrder, startIndex, limit, cancellationToken).ConfigureAwait(false);
        return Ok(proposals);
    }

    /// <summary>
    /// Counts proposals without returning proposal payloads.
    /// </summary>
    [HttpGet("Proposals/Count")]
    [ProducesResponseType<ProposalCountResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProposalCountResponse>> GetProposalCount(
        CancellationToken cancellationToken,
        [FromQuery] ProposalStatus? status = null)
    {
        var count = await _proposalStore.CountAsync(status, cancellationToken).ConfigureAwait(false);
        return Ok(new ProposalCountResponse { Count = count });
    }

    /// <summary>
    /// Gets one proposal.
    /// </summary>
    [HttpGet("Proposals/{proposalId:guid}")]
    [ProducesResponseType<MetadataProposal>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MetadataProposal>> GetProposal(
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        var proposal = await _proposalStore.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        return proposal is null
            ? NotFound(new ProblemDetails { Title = "Proposal not found" })
            : Ok(proposal);
    }

    /// <summary>
    /// Applies reviewer-selected fields from a pending proposal.
    /// </summary>
    [HttpPost("Proposals/{proposalId:guid}/Apply")]
    [RequestSizeLimit(512 * 1024)]
    [ProducesResponseType<MetadataProposal>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MetadataProposal>> ApplyProposal(
        Guid proposalId,
        [FromBody] ApplyProposalRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _metadataApplicationService.ApplyAsync(
                proposalId,
                request,
                cancellationToken).ConfigureAwait(false));
        }
        catch (KeyNotFoundException exception)
        {
            _logger.LogWarning(exception, "JellyCAP proposal {ProposalId} or its collection was not found during approval.", proposalId);
            return NotFound(new ProblemDetails { Title = "Proposal or collection not found", Detail = exception.Message });
        }
        catch (StaleProposalException exception)
        {
            _logger.LogWarning(exception, "JellyCAP proposal {ProposalId} is stale.", proposalId);
            return Conflict(new ProblemDetails { Title = "Proposal is stale", Detail = exception.Message });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _logger.LogError(exception, "JellyCAP proposal {ProposalId} could not be applied.", proposalId);
            return BadRequest(new ProblemDetails { Title = "Proposal could not be applied", Detail = exception.Message });
        }
    }

    /// <summary>
    /// Applies the generated changes from all pending proposals without forcing stale metadata.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Counts and bounded failure details for the bulk operation.</returns>
    [HttpPost("Proposals/ApplyPending")]
    [ProducesResponseType<BulkProposalApprovalResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BulkProposalApprovalResponse>> ApplyPendingProposals(
        CancellationToken cancellationToken)
    {
        var result = await _metadataApplicationService
            .ApplyAllPendingAsync(cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "JellyCAP bulk approval applied {AppliedProposalCount} pending proposals and left {FailedProposalCount} pending after failures.",
            result.AppliedCount,
            result.FailedCount);
        return Ok(result);
    }

    /// <summary>
    /// Rejects a pending proposal without updating Jellyfin metadata.
    /// </summary>
    [HttpPost("Proposals/{proposalId:guid}/Reject")]
    [ProducesResponseType<MetadataProposal>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MetadataProposal>> RejectProposal(
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _metadataApplicationService.RejectAsync(proposalId, cancellationToken).ConfigureAwait(false));
        }
        catch (KeyNotFoundException exception)
        {
            _logger.LogWarning(exception, "JellyCAP proposal {ProposalId} was not found during rejection.", proposalId);
            return NotFound(new ProblemDetails { Title = "Proposal not found", Detail = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogError(exception, "JellyCAP proposal {ProposalId} could not be rejected.", proposalId);
            return BadRequest(new ProblemDetails { Title = "Proposal could not be rejected", Detail = exception.Message });
        }
    }

    /// <summary>
    /// Rejects every pending proposal without updating Jellyfin metadata.
    /// </summary>
    [HttpPost("Proposals/RejectPending")]
    [ProducesResponseType<BulkProposalRejectionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BulkProposalRejectionResponse>> RejectPendingProposals(
        CancellationToken cancellationToken)
    {
        var rejectedCount = await _metadataApplicationService
            .RejectAllPendingAsync(cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "JellyCAP rejected {ProposalCount} pending proposals in bulk.",
            rejectedCount);
        return Ok(new BulkProposalRejectionResponse { RejectedCount = rejectedCount });
    }
}
