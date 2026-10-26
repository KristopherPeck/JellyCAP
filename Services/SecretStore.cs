using System.Text.Json;

namespace Jellyfin.Plugin.JellyAnnotator.Services;

/// <summary>
/// Persists provider credentials outside Jellyfin's publicly readable plugin configuration model.
/// </summary>
public sealed class SecretStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Resolves the effective API key and its source.
    /// </summary>
    /// <param name="environmentVariable">Optional environment variable name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The key and its source.</returns>
    public async Task<(string Key, string Source)> ResolveAsync(
        string? environmentVariable,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(environmentVariable))
        {
            var environmentKey = Environment.GetEnvironmentVariable(environmentVariable.Trim());
            if (!string.IsNullOrWhiteSpace(environmentKey))
            {
                return (environmentKey.Trim(), "environment");
            }
        }

        var storedKey = await ReadStoredKeyAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(storedKey)
            ? (string.Empty, "none")
            : (storedKey, "stored");
    }

    /// <summary>
    /// Replaces the stored provider API key.
    /// </summary>
    /// <param name="apiKey">The new API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WriteAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetSecretPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = JsonSerializer.Serialize(new SecretDocument { ApiKey = apiKey.Trim() }, JsonOptions);
            await WriteAtomicallyAsync(path, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the stored provider API key, if present.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetSecretPath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> ReadStoredKeyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetSecretPath();
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            var payload = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<SecretDocument>(payload, JsonOptions)?.ApiKey?.Trim() ?? string.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetSecretPath()
    {
        var dataFolder = Plugin.Instance?.DataFolderPath;
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            throw new InvalidOperationException("The JellyAnnotator data folder is not available.");
        }

        return Path.Combine(dataFolder, "secrets.json");
    }

    private static async Task WriteAtomicallyAsync(
        string destination,
        string content,
        CancellationToken cancellationToken)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed class SecretDocument
    {
        public string ApiKey { get; set; } = string.Empty;
    }
}

