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
    private const int DatabaseVersion = 1;
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
    /// <param name="startIndex">Zero-based index of the first result.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A bounded page, newest proposals first.</returns>
    public async Task<PagedResult<MetadataProposal>> ListAsync(
        ProposalStatus? status,
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
            command.CommandText = status.HasValue
                ? """
                    SELECT id, payload_json
                    FROM proposals
                    WHERE status = $status
                    ORDER BY created_at_utc DESC, id DESC
                    LIMIT $limit OFFSET $startIndex;
                    """
                : """
                    SELECT id, payload_json
                    FROM proposals
                    ORDER BY created_at_utc DESC, id DESC
                    LIMIT $limit OFFSET $startIndex;
                    """;
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
        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            var versionValue = await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var version = Convert.ToInt32(versionValue, CultureInfo.InvariantCulture);
            if (version > DatabaseVersion)
            {
                throw new InvalidOperationException(
                    $"The JellyCAP proposal database version {version} is newer than the supported version {DatabaseVersion}.");
            }
        }

        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = """
                CREATE TABLE IF NOT EXISTS proposals (
                    id TEXT NOT NULL PRIMARY KEY,
                    collection_id TEXT NOT NULL,
                    status INTEGER NOT NULL CHECK (status IN (0, 1, 2)),
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

                PRAGMA user_version = 1;
                """;
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await MigrateLegacyJsonUnsafeAsync(connection, cancellationToken).ConfigureAwait(false);
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
                status,
                created_at_utc,
                decided_at_utc,
                payload_json)
            VALUES (
                $id,
                $collectionId,
                $status,
                $createdAtUtc,
                $decidedAtUtc,
                $payloadJson)
            ON CONFLICT(id) DO UPDATE SET
                collection_id = excluded.collection_id,
                status = excluded.status,
                created_at_utc = excluded.created_at_utc,
                decided_at_utc = excluded.decided_at_utc,
                payload_json = excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$id", FormatGuid(proposal.Id));
        command.Parameters.AddWithValue("$collectionId", FormatGuid(proposal.CollectionId));
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
