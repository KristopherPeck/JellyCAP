using Jellyfin.Plugin.JellyCAP.Configuration;
using Jellyfin.Plugin.JellyCAP.Models;

namespace Jellyfin.Plugin.JellyCAP.Services;

/// <summary>
/// Orchestrates context collection, provider generation, and review proposal creation.
/// </summary>
public sealed class ProposalService
{
    private static readonly SemaphoreSlim ProviderGate = new(5, 5);

    private readonly CollectionContextService _collectionContextService;
    private readonly AiProviderClient _providerClient;
    private readonly SecretStore _secretStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProposalService"/> class.
    /// </summary>
    public ProposalService(
        CollectionContextService collectionContextService,
        AiProviderClient providerClient,
        SecretStore secretStore)
    {
        _collectionContextService = collectionContextService;
        _providerClient = providerClient;
        _secretStore = secretStore;
    }

    /// <summary>
    /// Generates a review proposal for a collection. The queue persists it atomically with job completion.
    /// </summary>
    /// <param name="collectionId">Collection identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The pending proposal.</returns>
    public async Task<MetadataProposal> GenerateAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        var configuration = SettingsValidator.Validate(
            Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("JellyCAP is not initialized."));
        var context = _collectionContextService.BuildContext(collectionId, configuration.MaximumContextItems);
        if (context.Items.Length == 0)
        {
            throw new InvalidOperationException("The collection contains no eligible items. Episodes and seasons are intentionally excluded.");
        }

        var usesAi = UsesAi(configuration);
        var generated = new GeneratedMetadata();
        if (usesAi)
        {
            await EnterProviderGateAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (apiKey, _) = await _secretStore.ResolveAsync(
                    configuration.ApiKeyEnvironmentVariable,
                    cancellationToken).ConfigureAwait(false);
                generated = await _providerClient.GenerateAsync(
                    context,
                    configuration,
                    apiKey,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ProviderGate.Release();
            }
        }
        var collection = _collectionContextService.GetCollection(collectionId);
        var original = MetadataFingerprint.Snapshot(collection);
        var proposed = ApplyPolicies(original, generated, context, configuration);
        var proposal = new MetadataProposal
        {
            CollectionId = collection.Id,
            CollectionName = collection.Name ?? string.Empty,
            Original = original,
            OriginalFingerprint = MetadataFingerprint.Compute(original),
            FingerprintVersion = MetadataFingerprint.CurrentVersion,
            Proposed = proposed,
            Provider = usesAi ? configuration.ProviderType.ToString() : "Jellyfin ratings",
            Model = usesAi ? configuration.Model : "Average",
            ContextItemCount = context.Items.Length,
            CommunityRatingSource = configuration.GenerateCommunityRating
                ? configuration.UseAiForCommunityRating
                    ? "AI suggestion"
                    : $"Average of {context.CommunityRatingItemCount} rated item(s)"
                : string.Empty
        };

        return proposal;
    }

    /// <summary>
    /// Performs a small real generation request using the saved provider configuration.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var configuration = (Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("JellyCAP is not initialized.")).Copy();
        configuration.GenerateOverview = true;
        configuration.GenerateGenres = false;
        configuration.GenerateTags = false;
        configuration.GenerateSortTitle = false;
        configuration.GenerateContentRating = false;
        configuration.GenerateCommunityRating = false;
        configuration = SettingsValidator.Validate(configuration);
        configuration.MaximumOverviewCharacters = 100;
        await EnterProviderGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (apiKey, _) = await _secretStore.ResolveAsync(
                configuration.ApiKeyEnvironmentVariable,
                cancellationToken).ConfigureAwait(false);
            var context = new CollectionContext
            {
                CollectionName = "Connection test",
                Items =
                [
                    new CollectionContextItem
                    {
                        Type = "Movie",
                        Name = "Connection test item",
                        Overview = "A harmless synthetic item used to verify provider configuration."
                    }
                ]
            };

            _ = await _providerClient.GenerateAsync(context, configuration, apiKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ProviderGate.Release();
        }
    }

    private static async Task EnterProviderGateAsync(CancellationToken cancellationToken)
    {
        if (!await ProviderGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            throw new GenerationCapacityException(
                "JellyCAP is already processing the maximum of five provider requests. Try again after an active request finishes.");
        }
    }

    private static GeneratedMetadata ApplyPolicies(
        MetadataSnapshot original,
        GeneratedMetadata generated,
        CollectionContext context,
        PluginConfiguration configuration)
    {
        var allowedGenres = ParseAllowedGenres(configuration, context);
        var generatedGenres = generated.Genres;
        if (allowedGenres.Length > 0)
        {
            generatedGenres = generatedGenres
                .Where(candidate => allowedGenres.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        generatedGenres = generatedGenres.Take(configuration.MaximumGenres).ToArray();
        var generatedTags = generated.Tags.Take(configuration.MaximumTags).ToArray();
        var warnings = generated.Warnings.ToList();
        var sortTitle = configuration.GenerateSortTitle
            ? ApplyScalarPolicy(
                original.SortTitle,
                NormalizeScalar(generated.SortTitle, 500),
                configuration.SortTitleUpdateMode)
            : original.SortTitle;
        var contentRating = original.ContentRating;
        if (configuration.GenerateContentRating)
        {
            var allowedRatings = context.Items
                .Select(item => item.ContentRating)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var generatedRating = NormalizeScalar(generated.ContentRating, 100);
            var canonicalRating = allowedRatings.FirstOrDefault(
                value => string.Equals(value, generatedRating, StringComparison.OrdinalIgnoreCase));
            if (allowedRatings.Length == 0)
            {
                warnings.Add("No child content ratings were available, so no content rating was suggested.");
            }
            else if (!string.IsNullOrWhiteSpace(generatedRating) && canonicalRating is null)
            {
                warnings.Add("The provider suggested a content rating not present on the child items; it was discarded.");
            }

            contentRating = ApplyScalarPolicy(
                original.ContentRating,
                canonicalRating ?? string.Empty,
                configuration.ContentRatingUpdateMode);
        }

        var overview = configuration.GenerateOverview
            ? ApplyScalarPolicy(
                original.Overview,
                NormalizeOverview(generated.Overview, configuration.MaximumOverviewCharacters),
                configuration.OverviewUpdateMode)
            : original.Overview;
        var communityRating = original.CommunityRating;
        if (configuration.GenerateCommunityRating)
        {
            float? suggestedRating;
            if (configuration.UseAiForCommunityRating)
            {
                suggestedRating = NormalizeCommunityRating(generated.CommunityRating);
                if (context.CommunityRatingItemCount == 0)
                {
                    warnings.Add("No child community ratings were available, so no community rating was suggested.");
                    suggestedRating = null;
                }
                else if (generated.CommunityRating.HasValue && !suggestedRating.HasValue)
                {
                    warnings.Add("The provider suggested a community rating outside Jellyfin's 0–10 range; it was discarded.");
                }
            }
            else
            {
                suggestedRating = context.AverageCommunityRating;
                if (!suggestedRating.HasValue)
                {
                    warnings.Add("No child community ratings were available to average.");
                }
            }

            communityRating = ApplyNullableScalarPolicy(
                original.CommunityRating,
                suggestedRating,
                configuration.CommunityRatingUpdateMode);
        }

        return new GeneratedMetadata
        {
            SortTitle = sortTitle,
            ContentRating = contentRating,
            CommunityRating = communityRating,
            Overview = overview,
            Genres = configuration.GenerateGenres
                ? Combine(original.Genres, generatedGenres, configuration.GenreUpdateMode)
                : original.Genres,
            Tags = configuration.GenerateTags
                ? Combine(original.Tags, generatedTags, configuration.TagUpdateMode)
                : original.Tags,
            Warnings = Normalize(warnings)
        };
    }

    private static string ApplyScalarPolicy(string existing, string generated, OverviewUpdateMode mode)
    {
        if (string.IsNullOrWhiteSpace(generated)
            || (mode == OverviewUpdateMode.OnlyIfEmpty && !string.IsNullOrWhiteSpace(existing)))
        {
            return existing;
        }

        return generated;
    }

    private static float? ApplyNullableScalarPolicy(float? existing, float? generated, OverviewUpdateMode mode)
    {
        if (!generated.HasValue
            || (mode == OverviewUpdateMode.OnlyIfEmpty && existing.HasValue))
        {
            return existing;
        }

        return generated;
    }

    private static float? NormalizeCommunityRating(float? rating)
    {
        return rating is >= 0 and <= 10 && float.IsFinite(rating.Value)
            ? (float)Math.Round(rating.Value, 1, MidpointRounding.AwayFromZero)
            : null;
    }

    private static bool UsesAi(PluginConfiguration configuration)
    {
        return configuration.GenerateOverview
            || configuration.GenerateGenres
            || configuration.GenerateTags
            || configuration.GenerateSortTitle
            || configuration.GenerateContentRating
            || (configuration.GenerateCommunityRating && configuration.UseAiForCommunityRating);
    }

    private static string[] Combine(string[] existing, string[] generated, ListUpdateMode mode)
    {
        return mode switch
        {
            ListUpdateMode.Replace => Normalize(generated),
            ListUpdateMode.OnlyIfEmpty => existing.Length == 0 ? Normalize(generated) : Normalize(existing),
            _ => Normalize(existing.Concat(generated))
        };
    }

    private static string[] ParseAllowedGenres(PluginConfiguration configuration, CollectionContext context)
    {
        var configured = configuration.GenreAllowList
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return configured.Length > 0
            ? configured
            : context.Items.SelectMany(item => item.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeOverview(string overview, int maximumCharacters)
    {
        var normalized = overview?.Trim() ?? string.Empty;
        if (normalized.Length <= maximumCharacters)
        {
            return normalized;
        }

        var shortened = normalized[..maximumCharacters].TrimEnd();
        var sentenceEnd = shortened.LastIndexOfAny(['.', '!', '?']);
        return sentenceEnd >= maximumCharacters / 2
            ? shortened[..(sentenceEnd + 1)]
            : shortened;
    }

    private static string NormalizeScalar(string? value, int maximumCharacters)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters].TrimEnd();
    }

    private static string[] Normalize(IEnumerable<string> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

/// <summary>
/// Indicates that the server-wide provider request safety limit has been reached.
/// </summary>
public sealed class GenerationCapacityException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GenerationCapacityException"/> class.
    /// </summary>
    /// <param name="message">The capacity failure message.</param>
    public GenerationCapacityException(string message)
        : base(message)
    {
    }
}
