using Jellyfin.Plugin.JellyCAP.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyCAP.Services;

/// <summary>
/// Runs durable generation jobs independently of the Jellyfin dashboard window.
/// </summary>
public sealed class GenerationQueueService : BackgroundService
{
    private const int MaximumWorkers = 5;

    private readonly SemaphoreSlim _wakeSignal = new(0, MaximumWorkers);
    private readonly CollectionContextService _collectionContextService;
    private readonly ProposalService _proposalService;
    private readonly ProposalStore _proposalStore;
    private readonly ILogger<GenerationQueueService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenerationQueueService"/> class.
    /// </summary>
    public GenerationQueueService(
        CollectionContextService collectionContextService,
        ProposalService proposalService,
        ProposalStore proposalStore,
        ILogger<GenerationQueueService> logger)
    {
        _collectionContextService = collectionContextService;
        _proposalService = proposalService;
        _proposalStore = proposalStore;
        _logger = logger;
    }

    /// <summary>
    /// Queues one collection, or returns it as already active.
    /// </summary>
    public Task<GenerationEnqueueResult> EnqueueAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        var collection = _collectionContextService.GetCollection(collectionId);
        return EnqueueAsync([(collection.Id, collection.Name ?? string.Empty)], cancellationToken);
    }

    /// <summary>
    /// Queues a set of collection identifiers.
    /// </summary>
    public async Task<GenerationEnqueueResult> EnqueueAsync(
        IEnumerable<Guid> collectionIds,
        CancellationToken cancellationToken)
    {
        var collections = collectionIds
            .Distinct()
            .Select(_collectionContextService.GetCollection)
            .Select(collection => (collection.Id, collection.Name ?? string.Empty))
            .ToArray();
        return await EnqueueAsync(collections, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var recovered = await _proposalStore.RecoverInterruptedGenerationJobsAsync(stoppingToken).ConfigureAwait(false);
            if (recovered > 0)
            {
                _logger.LogInformation(
                    "Returned {GenerationJobCount} interrupted JellyCAP generation jobs to the queue.",
                    recovered);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to recover interrupted JellyCAP generation jobs.");
        }

        await Task.WhenAll(
            Enumerable.Range(0, MaximumWorkers)
                .Select(_ => RunWorkerAsync(stoppingToken))).ConfigureAwait(false);
    }

    private async Task<GenerationEnqueueResult> EnqueueAsync(
        IReadOnlyCollection<(Guid CollectionId, string CollectionName)> collections,
        CancellationToken cancellationToken)
    {
        var result = await _proposalStore.EnqueueGenerationJobsAsync(collections, cancellationToken).ConfigureAwait(false);
        if (result.QueuedCount > 0)
        {
            WakeWorkers();
        }

        return result;
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var maximumConcurrent = Math.Clamp(
                    Plugin.Instance?.Configuration.MaximumConcurrentGenerations ?? 2,
                    1,
                    MaximumWorkers);
                var job = await _proposalStore.ClaimGenerationJobAsync(
                    maximumConcurrent,
                    stoppingToken).ConfigureAwait(false);
                if (job is null)
                {
                    await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The JellyCAP generation queue worker encountered an unexpected error.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessJobAsync(GenerationJob job, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation(
                "Generating JellyCAP proposal for collection {CollectionName} ({CollectionId}) from durable job {GenerationJobId}.",
                job.CollectionName,
                job.CollectionId,
                job.Id);
            var proposal = await _proposalService.GenerateAsync(job.CollectionId, stoppingToken).ConfigureAwait(false);
            await _proposalStore.CompleteGenerationJobAsync(
                job.Id,
                proposal,
                stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Leave the job running. Startup recovery returns it to the queue.
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "JellyCAP generation job {GenerationJobId} failed for collection {CollectionName} ({CollectionId}).",
                job.Id,
                job.CollectionName,
                job.CollectionId);
            try
            {
                await _proposalStore.FailGenerationJobAsync(
                    job.Id,
                    job.CollectionId,
                    exception.Message,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (Exception storageException) when (storageException is not OperationCanceledException)
            {
                _logger.LogError(
                    storageException,
                    "Failed to persist failure state for JellyCAP generation job {GenerationJobId}.",
                    job.Id);
            }
        }
        finally
        {
            WakeWorkers();
        }
    }

    private void WakeWorkers()
    {
        for (var index = 0; index < MaximumWorkers; index++)
        {
            try
            {
                _wakeSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                return;
            }
        }
    }
}
