using Jellyfin.Plugin.JellyAnnotator.Configuration;

namespace Jellyfin.Plugin.JellyAnnotator.Services;

/// <summary>
/// Validates and normalizes plugin settings before use.
/// </summary>
internal static class SettingsValidator
{
    /// <summary>
    /// Validates configuration and returns a normalized copy.
    /// </summary>
    /// <param name="source">Source configuration.</param>
    /// <returns>Normalized configuration.</returns>
    public static PluginConfiguration Validate(PluginConfiguration source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var configuration = source.Copy();

        configuration.Endpoint = configuration.Endpoint?.Trim() ?? string.Empty;
        configuration.Model = configuration.Model?.Trim() ?? string.Empty;
        configuration.ApiKeyEnvironmentVariable = configuration.ApiKeyEnvironmentVariable?.Trim() ?? string.Empty;
        configuration.OverviewStyle = configuration.OverviewStyle?.Trim() ?? string.Empty;
        configuration.Language = configuration.Language?.Trim() ?? string.Empty;
        configuration.GenreAllowList = configuration.GenreAllowList?.Trim() ?? string.Empty;
        configuration.CustomInstructions = configuration.CustomInstructions?.Trim() ?? string.Empty;

        if (!Enum.IsDefined(configuration.ProviderType)
            || !Enum.IsDefined(configuration.OverviewUpdateMode)
            || !Enum.IsDefined(configuration.GenreUpdateMode)
            || !Enum.IsDefined(configuration.TagUpdateMode)
            || !Enum.IsDefined(configuration.SortTitleUpdateMode)
            || !Enum.IsDefined(configuration.ContentRatingUpdateMode))
        {
            throw new ArgumentException("One or more configuration modes are invalid.", nameof(source));
        }

        if (string.IsNullOrWhiteSpace(configuration.Model))
        {
            throw new ArgumentException("A provider model is required.", nameof(source));
        }

        if (configuration.Model.Length > 200
            || configuration.Endpoint.Length > 2048
            || configuration.OverviewStyle.Length > 500
            || configuration.Language.Length > 100
            || configuration.GenreAllowList.Length > 5000
            || configuration.ApiKeyEnvironmentVariable.Length > 200)
        {
            throw new ArgumentException("One or more configuration values exceed their allowed length.", nameof(source));
        }

        if (configuration.ApiKeyEnvironmentVariable.Contains('=')
            || configuration.ApiKeyEnvironmentVariable.Contains('\0'))
        {
            throw new ArgumentException("The API key environment variable name is invalid.", nameof(source));
        }

        if (configuration.ProviderType == AiProviderType.OpenAiCompatible
            && string.IsNullOrWhiteSpace(configuration.Endpoint))
        {
            throw new ArgumentException("An endpoint is required for an OpenAI-compatible provider.", nameof(source));
        }

        if (!string.IsNullOrWhiteSpace(configuration.Endpoint))
        {
            if (!Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(endpoint.UserInfo)
                || !string.IsNullOrEmpty(endpoint.Fragment))
            {
                throw new ArgumentException("The provider endpoint must be an absolute HTTP or HTTPS URL without embedded credentials.", nameof(source));
            }
        }

        if (!configuration.GenerateOverview
            && !configuration.GenerateGenres
            && !configuration.GenerateTags
            && !configuration.GenerateSortTitle
            && !configuration.GenerateContentRating)
        {
            throw new ArgumentException("At least one metadata field must be enabled.", nameof(source));
        }

        configuration.MaximumOverviewCharacters = Math.Clamp(configuration.MaximumOverviewCharacters, 100, 10000);
        configuration.MaximumGenres = Math.Clamp(configuration.MaximumGenres, 1, 50);
        configuration.MaximumTags = Math.Clamp(configuration.MaximumTags, 1, 100);
        configuration.MaximumContextItems = Math.Clamp(configuration.MaximumContextItems, 1, 1000);
        configuration.RequestTimeoutSeconds = Math.Clamp(configuration.RequestTimeoutSeconds, 10, 600);
        configuration.MaximumConcurrentGenerations = Math.Clamp(configuration.MaximumConcurrentGenerations, 1, 5);
        configuration.CompletedProposalRetentionDays = Math.Clamp(configuration.CompletedProposalRetentionDays, 0, 3650);
        configuration.MaximumCompletedProposalHistory = Math.Clamp(configuration.MaximumCompletedProposalHistory, 0, 10000);

        if (configuration.CustomInstructions.Length > 4000)
        {
            throw new ArgumentException("Custom instructions cannot exceed 4,000 characters.", nameof(source));
        }

        return configuration;
    }
}

