using Jellyfin.Plugin.JellyCAP.Configuration;

namespace Jellyfin.Plugin.JellyCAP.Services;

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
            || !Enum.IsDefined(configuration.ContentRatingUpdateMode)
            || !Enum.IsDefined(configuration.CommunityRatingUpdateMode))
        {
            throw new ArgumentException("One or more configuration modes are invalid.", nameof(source));
        }

        var usesAi = configuration.GenerateOverview
            || configuration.GenerateGenres
            || configuration.GenerateTags
            || configuration.GenerateSortTitle
            || configuration.GenerateContentRating
            || (configuration.GenerateCommunityRating && configuration.UseAiForCommunityRating);
        if (usesAi && string.IsNullOrWhiteSpace(configuration.Model))
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

        if (!IsAllowedApiKeyEnvironmentVariable(configuration))
        {
            throw new ArgumentException(
                "The API key environment variable must begin with JELLYCAP_. OPENAI_API_KEY and ANTHROPIC_API_KEY are allowed only with their matching default provider endpoint.",
                nameof(source));
        }

        if (usesAi
            && configuration.ProviderType == AiProviderType.OpenAiCompatible
            && string.IsNullOrWhiteSpace(configuration.Endpoint))
        {
            throw new ArgumentException("An endpoint is required for an OpenAI-compatible provider.", nameof(source));
        }

        if (!string.IsNullOrWhiteSpace(configuration.Endpoint))
        {
            if (!Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(endpoint.UserInfo)
                || !string.IsNullOrEmpty(endpoint.Query)
                || !string.IsNullOrEmpty(endpoint.Fragment))
            {
                throw new ArgumentException("The provider endpoint must be an absolute HTTP or HTTPS URL without embedded credentials, a query string, or a fragment.", nameof(source));
            }

            if (endpoint.Scheme == Uri.UriSchemeHttp
                && !endpoint.IsLoopback
                && !configuration.AllowInsecureEndpoint)
            {
                throw new ArgumentException(
                    "Non-loopback HTTP provider endpoints require explicit insecure-endpoint permission because metadata and API keys would be sent without transport encryption.",
                    nameof(source));
            }
        }

        if (!configuration.GenerateOverview
            && !configuration.GenerateGenres
            && !configuration.GenerateTags
            && !configuration.GenerateSortTitle
            && !configuration.GenerateContentRating
            && !configuration.GenerateCommunityRating)
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

    /// <summary>
    /// Determines whether a configured environment-variable lookup is constrained to a plugin-owned
    /// name or the standard variable for an official provider using its default endpoint.
    /// </summary>
    public static bool IsAllowedApiKeyEnvironmentVariable(PluginConfiguration configuration)
    {
        var name = configuration.ApiKeyEnvironmentVariable?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return true;
        }

        var isPluginOwnedName = name.StartsWith("JELLYCAP_", StringComparison.Ordinal)
            && name.Length > "JELLYCAP_".Length
            && name.All(character => (character >= 'A' && character <= 'Z')
                || (character >= '0' && character <= '9')
                || character == '_');
        if (isPluginOwnedName)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(configuration.Endpoint))
        {
            return false;
        }

        return configuration.ProviderType switch
        {
            AiProviderType.OpenAi => string.Equals(name, "OPENAI_API_KEY", StringComparison.Ordinal),
            AiProviderType.Anthropic => string.Equals(name, "ANTHROPIC_API_KEY", StringComparison.Ordinal),
            _ => false
        };
    }
}

