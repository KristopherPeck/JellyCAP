using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyCAP.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyCAP.Services;

/// <summary>
/// Persists generated proposals and their review history in the plugin data folder.
/// </summary>
public sealed class ProposalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<ProposalStore> _logger;

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
            var document = await ReadAndPruneUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var matching = document.Proposals
                .Where(proposal => !status.HasValue || proposal.Status == status.Value)
                .OrderByDescending(proposal => proposal.CreatedAtUtc)
                .ToArray();
            var boundedStartIndex = Math.Max(0, startIndex);
            var boundedLimit = Math.Clamp(limit, 1, 100);
            return new PagedResult<MetadataProposal>
            {
                Items = matching.Skip(boundedStartIndex).Take(boundedLimit).ToArray(),
                TotalCount = matching.Length,
                StartIndex = boundedStartIndex,
                PageSize = boundedLimit
            };
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
            var document = await ReadAndPruneUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return document.Proposals.Count(proposal => !status.HasValue || proposal.Status == status.Value);
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
            var document = await ReadAndPruneUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return document.Proposals.FirstOrDefault(proposal => proposal.Id == id);
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
            var document = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var index = document.Proposals.FindIndex(candidate => candidate.Id == proposal.Id);
            if (index >= 0)
            {
                document.Proposals[index] = proposal;
            }
            else
            {
                document.Proposals.Add(proposal);
            }

            ApplyRetentionUnsafe(document);
            await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
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
            var document = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var removed = ApplyRetentionUnsafe(document);
            if (removed > 0)
            {
                await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Removed {ProposalCount} completed JellyCAP proposals according to retention settings.", removed);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetStorePath()
    {
        return Plugin.Instance?.GetDataFilePath("proposals.json")
            ?? throw new InvalidOperationException("The JellyCAP data folder is not available.");
    }

    private async Task<ProposalDocument> ReadAndPruneUnsafeAsync(CancellationToken cancellationToken)
    {
        var document = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        var removed = ApplyRetentionUnsafe(document);
        if (removed > 0)
        {
            await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Removed {ProposalCount} completed JellyCAP proposals according to retention settings.", removed);
        }

        return document;
    }

    private int ApplyRetentionUnsafe(ProposalDocument document)
    {
        var configuration = Plugin.Instance?.Configuration;
        var retentionDays = Math.Clamp(configuration?.CompletedProposalRetentionDays ?? 7, 0, 3650);
        var maximumHistory = Math.Clamp(configuration?.MaximumCompletedProposalHistory ?? 100, 0, 10000);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var retainedCompletedIds = document.Proposals
            .Where(proposal => proposal.Status != ProposalStatus.Pending)
            .Where(proposal => retentionDays > 0 && (proposal.DecidedAtUtc ?? proposal.CreatedAtUtc) >= cutoff)
            .OrderByDescending(proposal => proposal.DecidedAtUtc ?? proposal.CreatedAtUtc)
            .Take(maximumHistory)
            .Select(proposal => proposal.Id)
            .ToHashSet();

        return document.Proposals.RemoveAll(proposal =>
            proposal.Status != ProposalStatus.Pending && !retainedCompletedIds.Contains(proposal.Id));
    }

    private async Task<ProposalDocument> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        var path = GetStorePath();
        if (!File.Exists(path))
        {
            return new ProposalDocument();
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ProposalDocument>(content, JsonOptions) ?? new ProposalDocument();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Failed to read the JellyCAP proposal store at {ProposalStorePath}.", path);
            throw;
        }
    }

    private async Task WriteUnsafeAsync(ProposalDocument document, CancellationToken cancellationToken)
    {
        var path = GetStorePath();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = JsonSerializer.Serialize(document, JsonOptions);
            await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Failed to write the JellyCAP proposal store at {ProposalStorePath}.", path);
            throw;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed class ProposalDocument
    {
        public List<MetadataProposal> Proposals { get; set; } = [];
    }
}

