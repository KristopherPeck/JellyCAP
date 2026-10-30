using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyCAP.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyCAP.Services;

/// <summary>
/// Persists generated proposals and their review history in a plugin-owned SQLite database.
/// </summary>
public sealed class ProposalStore
{
    private const int DatabaseVersion = 3;
    private const string LegacyMigrationKey = "legacy-proposals-json-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly object ProviderInitializationLock = new();
    private static bool _providerInitialized;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<ProposalStore> _logger;
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProposalStore"/> class.
    /// </summary>
    /// <param name="logger">Jellyfin logger.</param>
    public ProposalStore(ILogger<ProposalStore> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Lists proposals, optionally filtered by status.
    /// </summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="sortOrder">Proposal ordering.</param>
    /// <param name="startIndex">Zero-based index of the first result.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A bounded page in the requested order.</returns>
    public async Task<PagedResult<MetadataProposal>> ListAsync(
        ProposalStatus? status,
        ProposalSortOrder sortOrder,
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await PruneAndLogUnsafeAsync(connection, cancellationToken).ConfigureAwait(false);

            var boundedStartIndex = Math.Max(0, startIndex);
            var boundedLimit = Math.Clamp(limit, 1, 100);
            var totalCount = await CountUnsafeAsync(connection, status, cancellationToken).ConfigureAwait(false);
            var items = new List<MetadataProposal>();

            await using var command = connection.CreateCommand();
            var orderBy = sortOrder switch
            {
                ProposalSortOrder.NameAscending => "collection_name COLLATE NOCASE ASC, created_at_utc DESC, id DESC",
                ProposalSortOrder.NameDescending => "collection_name COLLATE NOCASE DESC, created_at_utc DESC, id DESC",
                _ => "created_at_utc DESC, id DESC"
            };
            command.CommandText = status.HasValue
                ? $"SELECT id, payload_json FROM proposals WHERE status = $status ORDER BY {orderBy} LIMIT $limit OFFSET $startIndex;"
                : $"SELECT id, payload_json FROM proposals ORDER BY {orderBy} LIMIT $limit OFFSET $startIndex;";
            if (status.HasValue)
            {
                command.Parameters.AddWithValue("$status", (int)status.Value);
            }

            command.Parameters.AddWithValue("$limit", boundedLimit);
            command.Parameters.AddWithValue("$startIndex", boundedStartIndex);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(DeserializeProposal(reader.GetString(0), reader.GetString(1)));
            }

            return new PagedResult<MetadataProposal>
            {
                Items = items.ToArray(),
                TotalCount = totalCount,
                StartIndex = boundedStartIndex,
                PageSize = boundedLimit
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "list proposals");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Counts proposals, optionally filtered by status.
    /// </summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of matching proposals.</returns>
    public async Task<int> CountAsync(ProposalStatus? status, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await PruneAndLogUnsafeAsync(connection, cancellationToken).ConfigureAwait(false);
            return await CountUnsafeAsync(connection, status, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "count proposals");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gets a snapshot of every pending proposal for a bulk review decision.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Pending proposals ordered from newest to oldest.</returns>
    public async Task<MetadataProposal[]> ListPendingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await PruneAndLogUnsafeAsync(connection, cancellationToken).ConfigureAwait(false);
            var proposals = new List<MetadataProposal>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, payload_json FROM proposals WHERE status = $status ORDER BY created_at_utc DESC, id DESC;";
            command.Parameters.AddWithValue("$status", (int)ProposalStatus.Pending);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                proposals.Add(DeserializeProposal(reader.GetString(0), reader.GetString(1)));
            }

            return proposals.ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "list pending proposals for bulk approval");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Atomically marks every pending proposal as rejected.
    /// </summary>
    /// <param name="decidedAtUtc">Shared rejection timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of proposals rejected.</returns>
    public async Task<int> RejectAllPendingAsync(
        DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var proposals = new List<MetadataProposal>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT id, payload_json FROM proposals WHERE status = $status;";
                command.Parameters.AddWithValue("$status", (int)ProposalStatus.Pending);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    proposals.Add(DeserializeProposal(reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach (var proposal in proposals)
            {
                proposal.Status = ProposalStatus.Rejected;
                proposal.DecidedAtUtc = decidedAtUtc;
                await SaveUnsafeAsync(connection, transaction, proposal, cancellationToken).ConfigureAwait(false);
            }

            var removed = await ApplyRetentionUnsafeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            LogPruned(removed);
            return proposals.Count;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "reject all pending proposals");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gets one proposal by identifier.
    /// </summary>
    /// <param name="id">Proposal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The proposal, or null.</returns>
    public async Task<MetadataProposal?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await PruneAndLogUnsafeAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM proposals WHERE id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", FormatGuid(id));
            var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return payload is null ? null : DeserializeProposal(FormatGuid(id), payload);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "get a proposal");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Inserts or updates a proposal.
    /// </summary>
    /// <param name="proposal">Proposal to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveAsync(MetadataProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await SaveUnsafeAsync(connection, transaction, proposal, cancellationToken).ConfigureAwait(false);
            var removed = await ApplyRetentionUnsafeAsync(
                connection,
                transaction,
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            LogPruned(removed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "save a proposal");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Saves an applied proposal and its durable collection activity in one transaction.
    /// </summary>
    public async Task SaveAppliedAsync(MetadataProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Status != ProposalStatus.Applied || !proposal.DecidedAtUtc.HasValue)
        {
            throw new ArgumentException("The proposal must be applied and have a decision timestamp.", nameof(proposal));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await SaveUnsafeAsync(connection, transaction, proposal, cancellationToken).ConfigureAwait(false);
            await RecordAppliedUnsafeAsync(
                connection,
                transaction,
                proposal.CollectionId,
                proposal.DecidedAtUtc.Value,
                cancellationToken).ConfigureAwait(false);
            var removed = await ApplyRetentionUnsafeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            LogPruned(removed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "save an applied proposal");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Immediately applies the configured retention policy to completed proposals.
    /// Pending proposals are never removed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of completed proposals removed.</returns>
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return await PruneAndLogUnsafeAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "prune completed proposals");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds generation jobs while preserving the single active job per collection invariant.
    /// </summary>
    public async Task<GenerationEnqueueResult> EnqueueGenerationJobsAsync(
        IReadOnlyCollection<(Guid CollectionId, string CollectionName)> collections,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var result = new GenerationEnqueueResult();
            foreach (var collection in collections.DistinctBy(item => item.CollectionId))
            {
                await using (var removeOldCommand = connection.CreateCommand())
                {
                    removeOldCommand.Transaction = transaction;
                    removeOldCommand.CommandText = "DELETE FROM generation_jobs WHERE collection_id = $collectionId AND status IN ($completed, $failed);";
                    removeOldCommand.Parameters.AddWithValue("$collectionId", FormatGuid(collection.CollectionId));
                    removeOldCommand.Parameters.AddWithValue("$completed", (int)GenerationJobStatus.Completed);
                    removeOldCommand.Parameters.AddWithValue("$failed", (int)GenerationJobStatus.Failed);
                    await removeOldCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = """
                    INSERT OR IGNORE INTO generation_jobs (
                        id, collection_id, collection_name, status, queued_at_utc)
                    VALUES ($id, $collectionId, $collectionName, $status, $queuedAtUtc);
                    """;
                insertCommand.Parameters.AddWithValue("$id", FormatGuid(Guid.NewGuid()));
                insertCommand.Parameters.AddWithValue("$collectionId", FormatGuid(collection.CollectionId));
                insertCommand.Parameters.AddWithValue("$collectionName", Limit(collection.CollectionName, 500));
                insertCommand.Parameters.AddWithValue("$status", (int)GenerationJobStatus.Queued);
                insertCommand.Parameters.AddWithValue("$queuedAtUtc", FormatTimestamp(DateTimeOffset.UtcNow));
                if (await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0)
                {
                    result.QueuedCount++;
                }
                else
                {
                    result.AlreadyActiveCount++;
                }
            }

            transaction.Commit();
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "enqueue generation jobs");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Cancels queued jobs before a worker starts them.
    /// </summary>
    /// <param name="collectionId">Optional collection to cancel; null cancels every queued job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of jobs removed from the queue.</returns>
    public async Task<int> CancelQueuedGenerationJobsAsync(
        Guid? collectionId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = collectionId.HasValue
                ? "DELETE FROM generation_jobs WHERE status = $queued AND collection_id = $collectionId;"
                : "DELETE FROM generation_jobs WHERE status = $queued;";
            command.Parameters.AddWithValue("$queued", (int)GenerationJobStatus.Queued);
            if (collectionId.HasValue)
            {
                command.Parameters.AddWithValue("$collectionId", FormatGuid(collectionId.Value));
            }

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "cancel queued generation jobs");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Atomically claims the oldest queued job when the configured concurrency allows it.
    /// </summary>
    public async Task<GenerationJob?> ClaimGenerationJobAsync(
        int maximumConcurrentJobs,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await using (var countCommand = connection.CreateCommand())
            {
                countCommand.Transaction = transaction;
                countCommand.CommandText = "SELECT COUNT(*) FROM generation_jobs WHERE status = $running;";
                countCommand.Parameters.AddWithValue("$running", (int)GenerationJobStatus.Running);
                var running = Convert.ToInt32(
                    await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                if (running >= Math.Clamp(maximumConcurrentJobs, 1, 5))
                {
                    transaction.Commit();
                    return null;
                }
            }

            GenerationJob? job = null;
            await using (var selectCommand = connection.CreateCommand())
            {
                selectCommand.Transaction = transaction;
                selectCommand.CommandText = """
                    SELECT id, collection_id, collection_name, status, queued_at_utc,
                           started_at_utc, finished_at_utc, proposal_id, error
                    FROM generation_jobs
                    WHERE status = $queued
                    ORDER BY queued_at_utc, id
                    LIMIT 1;
                    """;
                selectCommand.Parameters.AddWithValue("$queued", (int)GenerationJobStatus.Queued);
                await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    job = ReadGenerationJob(reader);
                }
            }

            if (job is null)
            {
                transaction.Commit();
                return null;
            }

            var startedAt = DateTimeOffset.UtcNow;
            await using (var updateCommand = connection.CreateCommand())
            {
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = "UPDATE generation_jobs SET status = $running, started_at_utc = $startedAtUtc, error = '' WHERE id = $id AND status = $queued;";
                updateCommand.Parameters.AddWithValue("$running", (int)GenerationJobStatus.Running);
                updateCommand.Parameters.AddWithValue("$startedAtUtc", FormatTimestamp(startedAt));
                updateCommand.Parameters.AddWithValue("$id", FormatGuid(job.Id));
                updateCommand.Parameters.AddWithValue("$queued", (int)GenerationJobStatus.Queued);
                if (await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                {
                    transaction.Rollback();
                    return null;
                }
            }

            await UpsertAttemptUnsafeAsync(connection, transaction, job.CollectionId, startedAt, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            job.Status = GenerationJobStatus.Running;
            job.StartedAtUtc = startedAt;
            return job;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "claim a generation job");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Completes a job and increments the collection's durable successful-generation count.
    /// </summary>
    public async Task CompleteGenerationJobAsync(
        Guid jobId,
        MetadataProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            await SaveUnsafeAsync(connection, transaction, proposal, cancellationToken).ConfigureAwait(false);
            await CompleteGenerationJobUnsafeAsync(
                connection,
                transaction,
                jobId,
                proposal,
                cancellationToken).ConfigureAwait(false);
            var removed = await ApplyRetentionUnsafeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            LogPruned(removed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "save a proposal and complete its generation job");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks a job failed without incrementing the successful-generation count.
    /// </summary>
    public Task FailGenerationJobAsync(
        Guid jobId,
        Guid collectionId,
        string error,
        CancellationToken cancellationToken)
    {
        return FailGenerationJobCoreAsync(jobId, collectionId, error, cancellationToken);
    }

    /// <summary>
    /// Requeues jobs interrupted by a Jellyfin shutdown or plugin reload.
    /// </summary>
    public async Task<int> RecoverInterruptedGenerationJobsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE generation_jobs SET status = $queued, started_at_utc = NULL WHERE status = $running;";
            command.Parameters.AddWithValue("$queued", (int)GenerationJobStatus.Queued);
            command.Parameters.AddWithValue("$running", (int)GenerationJobStatus.Running);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gets aggregate queue state for dashboard polling.
    /// </summary>
    public async Task<GenerationQueueStatus> GetGenerationQueueStatusAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var result = new GenerationQueueStatus();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT status, COUNT(*) FROM generation_jobs WHERE status IN ($queued, $running) GROUP BY status;";
            command.Parameters.AddWithValue("$queued", (int)GenerationJobStatus.Queued);
            command.Parameters.AddWithValue("$running", (int)GenerationJobStatus.Running);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var count = reader.GetInt32(1);
                if ((GenerationJobStatus)reader.GetInt32(0) == GenerationJobStatus.Queued)
                {
                    result.QueuedCount = count;
                }
                else
                {
                    result.RunningCount = count;
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gets durable collection activity, including active jobs and pending reviews.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, CollectionGenerationActivity>> GetCollectionActivityAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<Guid, CollectionGenerationActivity>();
            await using (var historyCommand = connection.CreateCommand())
            {
                historyCommand.CommandText = "SELECT collection_id, generation_count, last_attempted_at_utc, last_generated_at_utc, last_applied_at_utc, last_generation_succeeded, last_generation_error, last_generation_had_changes FROM collection_generation_history;";
                await using var reader = await historyCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var collectionId = ParseGuid(reader.GetString(0));
                    result[collectionId] = new CollectionGenerationActivity
                    {
                        CollectionId = collectionId,
                        GenerationCount = reader.GetInt64(1),
                        LastAttemptedAtUtc = ReadTimestamp(reader, 2),
                        LastGeneratedAtUtc = ReadTimestamp(reader, 3),
                        LastAppliedAtUtc = ReadTimestamp(reader, 4),
                        LastGenerationSucceeded = reader.IsDBNull(5) ? null : reader.GetInt32(5) != 0,
                        LastGenerationError = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                        LastGenerationHadChanges = reader.IsDBNull(7) ? null : reader.GetInt32(7) != 0
                    };
                }
            }

            await using (var jobsCommand = connection.CreateCommand())
            {
                jobsCommand.CommandText = "SELECT collection_id, status FROM generation_jobs WHERE status IN ($queued, $running);";
                jobsCommand.Parameters.AddWithValue("$queued", (int)GenerationJobStatus.Queued);
                jobsCommand.Parameters.AddWithValue("$running", (int)GenerationJobStatus.Running);
                await using var reader = await jobsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var collectionId = ParseGuid(reader.GetString(0));
                    var activity = GetOrAddActivity(result, collectionId);
                    activity.ActiveJobStatus = (GenerationJobStatus)reader.GetInt32(1);
                }
            }

            await using (var pendingCommand = connection.CreateCommand())
            {
                pendingCommand.CommandText = "SELECT DISTINCT collection_id FROM proposals WHERE status = $pending;";
                pendingCommand.Parameters.AddWithValue("$pending", (int)ProposalStatus.Pending);
                await using var reader = await pendingCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var collectionId = ParseGuid(reader.GetString(0));
                    GetOrAddActivity(result, collectionId).HasPendingReview = true;
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task CompleteGenerationJobUnsafeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid jobId,
        MetadataProposal proposal,
        CancellationToken cancellationToken)
    {
        var finishedAt = DateTimeOffset.UtcNow;
        await using (var jobCommand = connection.CreateCommand())
        {
            jobCommand.Transaction = transaction;
            jobCommand.CommandText = """
                UPDATE generation_jobs
                SET status = $status,
                    finished_at_utc = $finishedAtUtc,
                    proposal_id = $proposalId,
                    error = ''
                WHERE id = $id AND collection_id = $collectionId AND status = $running;
                """;
            jobCommand.Parameters.AddWithValue("$status", (int)GenerationJobStatus.Completed);
            jobCommand.Parameters.AddWithValue("$finishedAtUtc", FormatTimestamp(finishedAt));
            jobCommand.Parameters.AddWithValue("$proposalId", FormatGuid(proposal.Id));
            jobCommand.Parameters.AddWithValue("$id", FormatGuid(jobId));
            jobCommand.Parameters.AddWithValue("$collectionId", FormatGuid(proposal.CollectionId));
            jobCommand.Parameters.AddWithValue("$running", (int)GenerationJobStatus.Running);
            if (await jobCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException($"Generation job '{jobId}' was not in a completable state.");
            }
        }

        await using var historyCommand = connection.CreateCommand();
        historyCommand.Transaction = transaction;
        historyCommand.CommandText = """
            INSERT INTO collection_generation_history (
                collection_id, generation_count, last_attempted_at_utc,
                last_generated_at_utc, last_generation_succeeded, last_generation_error,
                last_generation_had_changes)
            VALUES ($collectionId, 1, $finishedAtUtc, $finishedAtUtc, 1, '', $hadChanges)
            ON CONFLICT(collection_id) DO UPDATE SET
                generation_count = collection_generation_history.generation_count + 1,
                last_attempted_at_utc = excluded.last_attempted_at_utc,
                last_generated_at_utc = excluded.last_generated_at_utc,
                last_generation_succeeded = 1,
                last_generation_error = '',
                last_generation_had_changes = excluded.last_generation_had_changes;
            """;
        historyCommand.Parameters.AddWithValue("$collectionId", FormatGuid(proposal.CollectionId));
        historyCommand.Parameters.AddWithValue("$finishedAtUtc", FormatTimestamp(finishedAt));
        historyCommand.Parameters.AddWithValue("$hadChanges", proposal.Status == ProposalStatus.Unchanged ? 0 : 1);
        await historyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordAppliedUnsafeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid collectionId,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO collection_generation_history (collection_id, generation_count, last_applied_at_utc)
            VALUES ($collectionId, 0, $appliedAtUtc)
            ON CONFLICT(collection_id) DO UPDATE SET last_applied_at_utc = excluded.last_applied_at_utc;
            """;
        command.Parameters.AddWithValue("$collectionId", FormatGuid(collectionId));
        command.Parameters.AddWithValue("$appliedAtUtc", FormatTimestamp(appliedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FailGenerationJobCoreAsync(
        Guid jobId,
        Guid collectionId,
        string error,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionUnsafeAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            var finishedAt = DateTimeOffset.UtcNow;
            var boundedError = Limit(error, 1000);

            await using (var jobCommand = connection.CreateCommand())
            {
                jobCommand.Transaction = transaction;
                jobCommand.CommandText = """
                    UPDATE generation_jobs
                    SET status = $status,
                        finished_at_utc = $finishedAtUtc,
                        proposal_id = $proposalId,
                        error = $error
                    WHERE id = $id AND collection_id = $collectionId AND status = $running;
                    """;
                jobCommand.Parameters.AddWithValue(
                    "$status",
                    (int)GenerationJobStatus.Failed);
                jobCommand.Parameters.AddWithValue("$finishedAtUtc", FormatTimestamp(finishedAt));
                jobCommand.Parameters.AddWithValue("$proposalId", DBNull.Value);
                jobCommand.Parameters.AddWithValue("$error", boundedError);
                jobCommand.Parameters.AddWithValue("$id", FormatGuid(jobId));
                jobCommand.Parameters.AddWithValue("$collectionId", FormatGuid(collectionId));
                jobCommand.Parameters.AddWithValue("$running", (int)GenerationJobStatus.Running);
                if (await jobCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException($"Generation job '{jobId}' was not in a failable state.");
                }
            }

            await using (var historyCommand = connection.CreateCommand())
            {
                historyCommand.Transaction = transaction;
                historyCommand.CommandText = """
                    INSERT INTO collection_generation_history (
                        collection_id, generation_count, last_attempted_at_utc,
                        last_generation_succeeded, last_generation_error)
                    VALUES ($collectionId, 0, $finishedAtUtc, 0, $error)
                    ON CONFLICT(collection_id) DO UPDATE SET
                        last_attempted_at_utc = excluded.last_attempted_at_utc,
                        last_generation_succeeded = 0,
                        last_generation_error = excluded.last_generation_error,
                        last_generation_had_changes = NULL;
                    """;
                historyCommand.Parameters.AddWithValue("$collectionId", FormatGuid(collectionId));
                historyCommand.Parameters.AddWithValue("$finishedAtUtc", FormatTimestamp(finishedAt));
                historyCommand.Parameters.AddWithValue("$error", boundedError);
                await historyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStorageFailure(exception, "finish a generation job");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task UpsertAttemptUnsafeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid collectionId,
        DateTimeOffset attemptedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO collection_generation_history (
                collection_id, generation_count, last_attempted_at_utc, last_generation_error)
            VALUES ($collectionId, 0, $attemptedAtUtc, '')
            ON CONFLICT(collection_id) DO UPDATE SET
                last_attempted_at_utc = excluded.last_attempted_at_utc,
                last_generation_error = '';
            """;
        command.Parameters.AddWithValue("$collectionId", FormatGuid(collectionId));
        command.Parameters.AddWithValue("$attemptedAtUtc", FormatTimestamp(attemptedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static GenerationJob ReadGenerationJob(SqliteDataReader reader)
    {
        return new GenerationJob
        {
            Id = ParseGuid(reader.GetString(0)),
            CollectionId = ParseGuid(reader.GetString(1)),
            CollectionName = reader.GetString(2),
            Status = (GenerationJobStatus)reader.GetInt32(3),
            QueuedAtUtc = ParseTimestamp(reader.GetString(4)),
            StartedAtUtc = ReadTimestamp(reader, 5),
            FinishedAtUtc = ReadTimestamp(reader, 6),
            ProposalId = reader.IsDBNull(7) ? null : ParseGuid(reader.GetString(7)),
            Error = reader.IsDBNull(8) ? string.Empty : reader.GetString(8)
        };
    }

    private static CollectionGenerationActivity GetOrAddActivity(
        IDictionary<Guid, CollectionGenerationActivity> activity,
        Guid collectionId)
    {
        if (!activity.TryGetValue(collectionId, out var value))
        {
            value = new CollectionGenerationActivity { CollectionId = collectionId };
            activity.Add(collectionId, value);
        }

        return value;
    }

    private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static Guid ParseGuid(string value)
    {
        return Guid.ParseExact(value, "N");
    }

    private static string Limit(string? value, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static string GetDatabasePath()
    {
        return Plugin.Instance?.GetDataFilePath("jellycap.db")
            ?? throw new InvalidOperationException("The JellyCAP data folder is not available.");
    }

    private static string GetLegacyStorePath()
    {
        return Plugin.Instance?.GetDataFilePath("proposals.json")
            ?? throw new InvalidOperationException("The JellyCAP data folder is not available.");
    }

    private async Task<SqliteConnection> OpenConnectionUnsafeAsync(CancellationToken cancellationToken)
    {
        EnsureSqliteProviderInitialized();
        var databasePath = GetDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
        var connection = new SqliteConnection(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionUnsafeAsync(connection, cancellationToken).ConfigureAwait(false);
            if (!_initialized)
            {
                await InitializeUnsafeAsync(connection, cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void EnsureSqliteProviderInitialized()
    {
        lock (ProviderInitializationLock)
        {
            if (_providerInitialized)
            {
                return;
            }

            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
            SQLitePCL.raw.FreezeProvider();
            _providerInitialized = true;
        }
    }

    private static async Task ConfigureConnectionUnsafeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout = 30000;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeUnsafeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var previousVersion = 0;
        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            var versionValue = await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            previousVersion = Convert.ToInt32(versionValue, CultureInfo.InvariantCulture);
            if (previousVersion > DatabaseVersion)
            {
                throw new InvalidOperationException(
                    $"The JellyCAP proposal database version {previousVersion} is newer than the supported version {DatabaseVersion}.");
            }
        }

        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = """
                CREATE TABLE IF NOT EXISTS proposals (
                    id TEXT NOT NULL PRIMARY KEY,
                    collection_id TEXT NOT NULL,
                    collection_name TEXT NOT NULL,
                    status INTEGER NOT NULL CHECK (status IN (0, 1, 2, 3)),
                    created_at_utc TEXT NOT NULL,
                    decided_at_utc TEXT NULL,
                    payload_json TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_proposals_status_created
                    ON proposals (status, created_at_utc DESC);

                CREATE INDEX IF NOT EXISTS idx_proposals_completed_decided
                    ON proposals (status, decided_at_utc DESC, created_at_utc DESC);

                CREATE TABLE IF NOT EXISTS store_metadata (
                    key TEXT NOT NULL PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS generation_jobs (
                    id TEXT NOT NULL PRIMARY KEY,
                    collection_id TEXT NOT NULL,
                    collection_name TEXT NOT NULL,
                    status INTEGER NOT NULL CHECK (status IN (0, 1, 2, 3)),
                    queued_at_utc TEXT NOT NULL,
                    started_at_utc TEXT NULL,
                    finished_at_utc TEXT NULL,
                    proposal_id TEXT NULL,
                    error TEXT NOT NULL DEFAULT ''
                );

                CREATE UNIQUE INDEX IF NOT EXISTS idx_generation_jobs_active_collection
                    ON generation_jobs (collection_id)
                    WHERE status IN (0, 1);

                CREATE INDEX IF NOT EXISTS idx_generation_jobs_status_queued
                    ON generation_jobs (status, queued_at_utc, id);

                CREATE TABLE IF NOT EXISTS collection_generation_history (
                    collection_id TEXT NOT NULL PRIMARY KEY,
                    generation_count INTEGER NOT NULL DEFAULT 0,
                    last_attempted_at_utc TEXT NULL,
                    last_generated_at_utc TEXT NULL,
                    last_applied_at_utc TEXT NULL,
                    last_generation_succeeded INTEGER NULL,
                    last_generation_error TEXT NOT NULL DEFAULT '',
                    last_generation_had_changes INTEGER NULL
                );

                """;
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (previousVersion > 0 && previousVersion < 3)
        {
            using var migrationTransaction = connection.BeginTransaction();
            await using var proposalMigrationCommand = connection.CreateCommand();
            proposalMigrationCommand.Transaction = migrationTransaction;
            proposalMigrationCommand.CommandText = """
                DROP INDEX IF EXISTS idx_proposals_status_created;
                DROP INDEX IF EXISTS idx_proposals_completed_decided;
                ALTER TABLE proposals RENAME TO proposals_before_v3;
                CREATE TABLE proposals (
                    id TEXT NOT NULL PRIMARY KEY,
                    collection_id TEXT NOT NULL,
                    collection_name TEXT NOT NULL,
                    status INTEGER NOT NULL CHECK (status IN (0, 1, 2, 3)),
                    created_at_utc TEXT NOT NULL,
                    decided_at_utc TEXT NULL,
                    payload_json TEXT NOT NULL
                );
                INSERT INTO proposals (id, collection_id, collection_name, status, created_at_utc, decided_at_utc, payload_json)
                SELECT id, collection_id, COALESCE(json_extract(payload_json, '$.collectionName'), ''), status, created_at_utc, decided_at_utc, payload_json
                FROM proposals_before_v3;
                DROP TABLE proposals_before_v3;
                CREATE INDEX idx_proposals_status_created ON proposals (status, created_at_utc DESC);
                CREATE INDEX idx_proposals_completed_decided ON proposals (status, decided_at_utc DESC, created_at_utc DESC);
                """;
            await proposalMigrationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var hasHistoryOutcomeColumn = false;
            await using (var historyColumnsCommand = connection.CreateCommand())
            {
                historyColumnsCommand.Transaction = migrationTransaction;
                historyColumnsCommand.CommandText = "PRAGMA table_info(collection_generation_history);";
                await using var reader = await historyColumnsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (string.Equals(reader.GetString(1), "last_generation_had_changes", StringComparison.OrdinalIgnoreCase))
                    {
                        hasHistoryOutcomeColumn = true;
                        break;
                    }
                }
            }

            if (!hasHistoryOutcomeColumn)
            {
                await using var historyOutcomeMigrationCommand = connection.CreateCommand();
                historyOutcomeMigrationCommand.Transaction = migrationTransaction;
                historyOutcomeMigrationCommand.CommandText = "ALTER TABLE collection_generation_history ADD COLUMN last_generation_had_changes INTEGER NULL;";
                await historyOutcomeMigrationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            migrationTransaction.Commit();
        }

        await using (var proposalNameIndexCommand = connection.CreateCommand())
        {
            proposalNameIndexCommand.CommandText = "CREATE INDEX IF NOT EXISTS idx_proposals_status_collection_name ON proposals (status, collection_name COLLATE NOCASE);";
            await proposalNameIndexCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await MigrateLegacyJsonUnsafeAsync(connection, cancellationToken).ConfigureAwait(false);

        if (previousVersion < 2)
        {
            await using var historyMigrationCommand = connection.CreateCommand();
            historyMigrationCommand.CommandText = """
                INSERT OR IGNORE INTO collection_generation_history (
                    collection_id,
                    generation_count,
                    last_attempted_at_utc,
                    last_generated_at_utc,
                    last_applied_at_utc,
                    last_generation_succeeded,
                    last_generation_error)
                SELECT
                    collection_id,
                    COUNT(*),
                    MAX(created_at_utc),
                    MAX(created_at_utc),
                    MAX(CASE WHEN status = 1 THEN decided_at_utc ELSE NULL END),
                    1,
                    ''
                FROM proposals
                GROUP BY collection_id;
                """;
            await historyMigrationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var setVersionCommand = connection.CreateCommand())
        {
            setVersionCommand.CommandText = "PRAGMA user_version = 3;";
            await setVersionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Initialized the JellyCAP proposal database at {ProposalDatabasePath} with schema version {DatabaseVersion}.",
            GetDatabasePath(),
            DatabaseVersion);
    }

    private async Task MigrateLegacyJsonUnsafeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var markerCommand = connection.CreateCommand())
        {
            markerCommand.CommandText = "SELECT 1 FROM store_metadata WHERE key = $key LIMIT 1;";
            markerCommand.Parameters.AddWithValue("$key", LegacyMigrationKey);
            if (await markerCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return;
            }
        }

        var legacyPath = GetLegacyStorePath();
        ProposalDocument document;
        if (File.Exists(legacyPath))
        {
            try
            {
                var content = await File.ReadAllTextAsync(legacyPath, cancellationToken).ConfigureAwait(false);
                document = JsonSerializer.Deserialize<ProposalDocument>(content, JsonOptions) ?? new ProposalDocument();
                document.Proposals ??= [];
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(
                    exception,
                    "Failed to read the legacy JellyCAP proposal store at {LegacyProposalStorePath}; SQLite migration was stopped.",
                    legacyPath);
                throw;
            }
        }
        else
        {
            document = new ProposalDocument();
        }

        using var transaction = connection.BeginTransaction();
        foreach (var proposal in document.Proposals)
        {
            await SaveUnsafeAsync(connection, transaction, proposal, cancellationToken).ConfigureAwait(false);
        }

        await using (var markerCommand = connection.CreateCommand())
        {
            markerCommand.Transaction = transaction;
            markerCommand.CommandText = "INSERT INTO store_metadata (key, value) VALUES ($key, $value);";
            markerCommand.Parameters.AddWithValue("$key", LegacyMigrationKey);
            markerCommand.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await markerCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
        if (!File.Exists(legacyPath))
        {
            return;
        }

        var backupPath = legacyPath + ".migrated.bak";
        try
        {
            if (File.Exists(backupPath))
            {
                _logger.LogWarning(
                    "Imported {ProposalCount} legacy JellyCAP proposals, but backup path {LegacyBackupPath} already exists. The original JSON file was left in place.",
                    document.Proposals.Count,
                    backupPath);
                return;
            }

            File.Move(legacyPath, backupPath);
            _logger.LogInformation(
                "Migrated {ProposalCount} JellyCAP proposals to SQLite and retained the legacy store at {LegacyBackupPath}.",
                document.Proposals.Count,
                backupPath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Migrated {ProposalCount} JellyCAP proposals to SQLite, but could not rename the legacy JSON store at {LegacyProposalStorePath}. The original file was left in place.",
                document.Proposals.Count,
                legacyPath);
        }
    }

    private static async Task SaveUnsafeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MetadataProposal proposal,
        CancellationToken cancellationToken)
    {
        ValidateProposal(proposal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO proposals (
                id,
                collection_id,
                collection_name,
                status,
                created_at_utc,
                decided_at_utc,
                payload_json)
            VALUES (
                $id,
                $collectionId,
                $collectionName,
                $status,
                $createdAtUtc,
                $decidedAtUtc,
                $payloadJson)
            ON CONFLICT(id) DO UPDATE SET
                collection_id = excluded.collection_id,
                collection_name = excluded.collection_name,
                status = excluded.status,
                created_at_utc = excluded.created_at_utc,
                decided_at_utc = excluded.decided_at_utc,
                payload_json = excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$id", FormatGuid(proposal.Id));
        command.Parameters.AddWithValue("$collectionId", FormatGuid(proposal.CollectionId));
        command.Parameters.AddWithValue("$collectionName", proposal.CollectionName);
        command.Parameters.AddWithValue("$status", (int)proposal.Status);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            proposal.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$decidedAtUtc",
            proposal.DecidedAtUtc.HasValue
                ? proposal.DecidedAtUtc.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                : DBNull.Value);
        command.Parameters.AddWithValue("$payloadJson", JsonSerializer.Serialize(proposal, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CountUnsafeAsync(
        SqliteConnection connection,
        ProposalStatus? status,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = status.HasValue
            ? "SELECT COUNT(*) FROM proposals WHERE status = $status;"
            : "SELECT COUNT(*) FROM proposals;";
        if (status.HasValue)
        {
            command.Parameters.AddWithValue("$status", (int)status.Value);
        }

        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private async Task<int> PruneAndLogUnsafeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        var removed = await ApplyRetentionUnsafeAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        LogPruned(removed);
        return removed;
    }

    private static async Task<int> ApplyRetentionUnsafeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        var retentionDays = Math.Clamp(configuration?.CompletedProposalRetentionDays ?? 7, 0, 3650);
        var maximumHistory = Math.Clamp(configuration?.MaximumCompletedProposalHistory ?? 100, 0, 10000);
        var removed = 0;

        if (retentionDays == 0 || maximumHistory == 0)
        {
            await using var removeAllCommand = connection.CreateCommand();
            removeAllCommand.Transaction = transaction;
            removeAllCommand.CommandText = "DELETE FROM proposals WHERE status <> $pending;";
            removeAllCommand.Parameters.AddWithValue("$pending", (int)ProposalStatus.Pending);
            removed = await removeAllCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using (var ageCommand = connection.CreateCommand())
            {
                ageCommand.Transaction = transaction;
                ageCommand.CommandText = """
                    DELETE FROM proposals
                    WHERE status <> $pending
                      AND COALESCE(decided_at_utc, created_at_utc) < $cutoff;
                    """;
                ageCommand.Parameters.AddWithValue("$pending", (int)ProposalStatus.Pending);
                ageCommand.Parameters.AddWithValue(
                    "$cutoff",
                    DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O", CultureInfo.InvariantCulture));
                removed += await ageCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var countCommand = connection.CreateCommand())
            {
                countCommand.Transaction = transaction;
                countCommand.CommandText = """
                    DELETE FROM proposals
                    WHERE status <> $pending
                      AND id NOT IN (
                          SELECT id
                          FROM proposals
                          WHERE status <> $pending
                          ORDER BY COALESCE(decided_at_utc, created_at_utc) DESC, id DESC
                          LIMIT $maximumHistory
                      );
                    """;
                countCommand.Parameters.AddWithValue("$pending", (int)ProposalStatus.Pending);
                countCommand.Parameters.AddWithValue("$maximumHistory", maximumHistory);
                removed += await countCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return removed;
    }

    private void LogPruned(int removed)
    {
        if (removed > 0)
        {
            _logger.LogInformation(
                "Removed {ProposalCount} completed JellyCAP proposals from SQLite according to retention settings.",
                removed);
        }
    }

    private static MetadataProposal DeserializeProposal(string id, string payload)
    {
        try
        {
            var proposal = JsonSerializer.Deserialize<MetadataProposal>(payload, JsonOptions)
                ?? throw new InvalidDataException($"Stored proposal '{id}' has an empty payload.");
            ValidateProposal(proposal);
            if (!string.Equals(FormatGuid(proposal.Id), id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Stored proposal '{id}' has a mismatched payload identifier.");
            }

            return proposal;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Stored proposal '{id}' contains invalid JSON.", exception);
        }
    }

    private static void ValidateProposal(MetadataProposal proposal)
    {
        if (proposal.Id == Guid.Empty || proposal.CollectionId == Guid.Empty)
        {
            throw new InvalidDataException("A proposal and its collection must have non-empty identifiers.");
        }

        if (!Enum.IsDefined(proposal.Status))
        {
            throw new InvalidDataException($"Proposal '{proposal.Id}' has an invalid status.");
        }
    }

    private static string FormatGuid(Guid value)
    {
        return value.ToString("N", CultureInfo.InvariantCulture);
    }

    private void LogStorageFailure(Exception exception, string operation)
    {
        _logger.LogError(
            exception,
            "Failed to {ProposalStoreOperation} in the JellyCAP SQLite database at {ProposalDatabasePath}.",
            operation,
            GetDatabasePath());
    }

    private sealed class ProposalDocument
    {
        public List<MetadataProposal> Proposals { get; set; } = [];
    }
}
