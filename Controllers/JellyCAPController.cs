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
[Route("JellyCAP")]
public sealed class JellyCAPController : ControllerBase
{
    private readonly CollectionContextService _collectionContextService;
    private readonly MetadataApplicationService _metadataApplicationService;
    private readonly ProposalService _proposalService;
    private readonly ProposalStore _proposalStore;
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
        SecretStore secretStore,
        ILogger<JellyCAPController> logger)
    {
        _collectionContextService = collectionContextService;
        _metadataApplicationService = metadataApplicationService;
        _proposalService = proposalService;
        _proposalStore = proposalStore;
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
        var (apiKey, source) = await _secretStore.ResolveAsync(
            configuration.ApiKeyEnvironmentVariable,
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
    [ProducesResponseType<SettingsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SettingsResponse>> UpdateSettings(
        [FromBody] SettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var configuration = SettingsValidator.Validate(request.Configuration);
            var plugin = Plugin.Instance
                ?? throw new InvalidOperationException("JellyCAP is not initialized.");
            plugin.UpdateConfiguration(configuration);

            if (request.ClearStoredApiKey)
            {
                await _secretStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                await _secretStore.WriteAsync(request.ApiKey.Trim(), cancellationToken).ConfigureAwait(false);
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
    [ProducesResponseType<ConnectionTestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ConnectionTestResponse>> TestConnection(CancellationToken cancellationToken)
    {
        try
        {
            await _proposalService.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new ConnectionTestResponse { Success = true, Message = "Provider connection succeeded." });
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
    public ActionResult<PagedResult<CollectionSummary>> GetCollections(
        [FromQuery] string? search = null,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50)
    {
        try
        {
            return Ok(_collectionContextService.ListCollections(search, startIndex, limit));
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
    /// Generates a pending metadata proposal for one collection.
    /// </summary>
    [HttpPost("Collections/{collectionId:guid}/Generate")]
    [ProducesResponseType<MetadataProposal>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<MetadataProposal>> Generate(
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _proposalService.GenerateAsync(collectionId, cancellationToken).ConfigureAwait(false));
        }
        catch (KeyNotFoundException exception)
        {
            _logger.LogWarning(exception, "JellyCAP collection {CollectionId} was not found during generation.", collectionId);
            return NotFound(new ProblemDetails { Title = "Collection not found", Detail = exception.Message });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "JellyCAP proposal generation failed for collection {CollectionId}.", collectionId);
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new ProblemDetails { Title = "Generation failed", Detail = exception.Message });
        }
    }

    /// <summary>
    /// Lists proposals, optionally filtered by lifecycle status.
    /// </summary>
    [HttpGet("Proposals")]
    [ProducesResponseType<PagedResult<MetadataProposal>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<MetadataProposal>>> GetProposals(
        CancellationToken cancellationToken,
        [FromQuery] ProposalStatus? status = null,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 25)
    {
        var proposals = await _proposalStore.ListAsync(status, startIndex, limit, cancellationToken).ConfigureAwait(false);
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
}
