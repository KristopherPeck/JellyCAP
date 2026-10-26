using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyAnnotator.Models;

namespace Jellyfin.Plugin.JellyAnnotator.Services;

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

    /// <summary>
    /// Lists proposals, optionally filtered by status.
    /// </summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Newest proposals first.</returns>
    public async Task<IReadOnlyList<MetadataProposal>> ListAsync(
        ProposalStatus? status,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return document.Proposals
                .Where(proposal => !status.HasValue || proposal.Status == status.Value)
                .OrderByDescending(proposal => proposal.CreatedAtUtc)
                .ToArray();
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
            var document = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
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

            await WriteUnsafeAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetStorePath()
    {
        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            throw new InvalidOperationException("The JellyAnnotator data folder is not available.");
        }

        return Path.Combine(dataFolder, "proposals.json");
    }

    private static async Task<ProposalDocument> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        var path = GetStorePath();
        if (!File.Exists(path))
        {
            return new ProposalDocument();
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ProposalDocument>(content, JsonOptions) ?? new ProposalDocument();
    }

    private static async Task WriteUnsafeAsync(ProposalDocument document, CancellationToken cancellationToken)
    {
        var path = GetStorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = JsonSerializer.Serialize(document, JsonOptions);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
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

