using System.Text.Json;

namespace Jellyfin.Plugin.JellyCAP.Services;

/// <summary>
/// Persists provider credentials outside Jellyfin's publicly readable plugin configuration model.
/// </summary>
public sealed class SecretStore
{
    private const int MaximumApiKeyLength = 8192;

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
                return (ValidateApiKey(environmentKey), "environment");
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
        var validatedApiKey = ValidateApiKey(apiKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetSecretPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = JsonSerializer.Serialize(new SecretDocument { ApiKey = validatedApiKey }, JsonOptions);
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

            ApplyRestrictedPermissions(path);
            var payload = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var apiKey = JsonSerializer.Deserialize<SecretDocument>(payload, JsonOptions)?.ApiKey;
            return string.IsNullOrWhiteSpace(apiKey) ? string.Empty : ValidateApiKey(apiKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetSecretPath()
    {
        return Plugin.Instance?.GetDataFilePath("secrets.json")
            ?? throw new InvalidOperationException("The JellyCAP data folder is not available.");
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
            ApplyRestrictedPermissions(temporary);

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

    internal static string ValidateApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var normalized = apiKey.Trim();
        if (normalized.Length > MaximumApiKeyLength
            || normalized.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException(
                $"The API key must be at most {MaximumApiKeyLength} characters and cannot contain control characters.",
                nameof(apiKey));
        }

        return normalized;
    }

    private static void ApplyRestrictedPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed class SecretDocument
    {
        public string ApiKey { get; set; } = string.Empty;
    }
}

