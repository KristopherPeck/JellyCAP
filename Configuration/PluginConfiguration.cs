using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyCAP.Configuration;

/// <summary>
/// Non-secret JellyCAP configuration. Provider credentials are stored separately and are
/// never returned through Jellyfin's standard plugin configuration endpoint.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the provider protocol.
    /// </summary>
    public AiProviderType ProviderType { get; set; } = AiProviderType.OpenAi;

    /// <summary>
    /// Gets or sets the endpoint or base URL. An empty value uses the provider default where one exists.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model identifier passed to the provider.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional environment variable from which to read the provider API key.
    /// This takes precedence over the key stored in the plugin data folder.
    /// </summary>
    public string ApiKeyEnvironmentVariable { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether overviews are generated.
    /// </summary>
    public bool GenerateOverview { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether genres are generated.
    /// </summary>
    public bool GenerateGenres { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether tags are generated.
    /// </summary>
    public bool GenerateTags { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether sort titles are generated.
    /// </summary>
    public bool GenerateSortTitle { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether content ratings are generated.
    /// </summary>
    public bool GenerateContentRating { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether community ratings are generated.
    /// </summary>
    public bool GenerateCommunityRating { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether AI suggests the community rating instead of using the child-item average.
    /// </summary>
    public bool UseAiForCommunityRating { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the dashboard opens pending reviews after generation.
    /// </summary>
    public bool OpenReviewAfterGeneration { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of collection proposals generated concurrently per dashboard session.
    /// Additional requests remain queued in that dashboard session.
    /// </summary>
    public int MaximumConcurrentGenerations { get; set; } = 2;

    /// <summary>
    /// Gets or sets the overview replacement policy.
    /// </summary>
    public OverviewUpdateMode OverviewUpdateMode { get; set; } = OverviewUpdateMode.Replace;

    /// <summary>
    /// Gets or sets the genre combination policy.
    /// </summary>
    public ListUpdateMode GenreUpdateMode { get; set; } = ListUpdateMode.Merge;

    /// <summary>
    /// Gets or sets the tag combination policy.
    /// </summary>
    public ListUpdateMode TagUpdateMode { get; set; } = ListUpdateMode.Merge;

    /// <summary>
    /// Gets or sets the sort title replacement policy.
    /// </summary>
    public OverviewUpdateMode SortTitleUpdateMode { get; set; } = OverviewUpdateMode.Replace;

    /// <summary>
    /// Gets or sets the content rating replacement policy.
    /// </summary>
    public OverviewUpdateMode ContentRatingUpdateMode { get; set; } = OverviewUpdateMode.Replace;

    /// <summary>
    /// Gets or sets the community rating replacement policy.
    /// </summary>
    public OverviewUpdateMode CommunityRatingUpdateMode { get; set; } = OverviewUpdateMode.Replace;

    /// <summary>
    /// Gets or sets the desired overview style.
    /// </summary>
    public string OverviewStyle { get; set; } = "Neutral and concise";

    /// <summary>
    /// Gets or sets the output language.
    /// </summary>
    public string Language { get; set; } = "English";

    /// <summary>
    /// Gets or sets a value indicating whether major spoilers are allowed.
    /// </summary>
    public bool AllowSpoilers { get; set; }

    /// <summary>
    /// Gets or sets the maximum overview length in characters.
    /// </summary>
    public int MaximumOverviewCharacters { get; set; } = 1200;

    /// <summary>
    /// Gets or sets the maximum number of genres.
    /// </summary>
    public int MaximumGenres { get; set; } = 6;

    /// <summary>
    /// Gets or sets the maximum number of tags.
    /// </summary>
    public int MaximumTags { get; set; } = 12;

    /// <summary>
    /// Gets or sets the maximum number of collection items sent to a provider.
    /// </summary>
    public int MaximumContextItems { get; set; } = 200;

    /// <summary>
    /// Gets or sets the provider timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Gets or sets how many days applied and rejected proposals are retained.
    /// Zero removes completed proposals immediately.
    /// </summary>
    public int CompletedProposalRetentionDays { get; set; } = 7;

    /// <summary>
    /// Gets or sets the maximum combined number of applied and rejected proposals retained.
    /// Zero keeps no completed proposal history.
    /// </summary>
    public int MaximumCompletedProposalHistory { get; set; } = 100;

    /// <summary>
    /// Gets or sets an optional newline- or comma-delimited genre allow-list.
    /// When empty, genres found on the collection's children are used as the vocabulary.
    /// </summary>
    public string GenreAllowList { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional extra instructions appended to the generation prompt.
    /// </summary>
    public string CustomInstructions { get; set; } = string.Empty;

    /// <summary>
    /// Creates a detached configuration copy suitable for validation and persistence.
    /// </summary>
    /// <returns>A copy of this configuration.</returns>
    public PluginConfiguration Copy()
    {
        return new PluginConfiguration
        {
            ProviderType = ProviderType,
            Endpoint = Endpoint,
            Model = Model,
            ApiKeyEnvironmentVariable = ApiKeyEnvironmentVariable,
            GenerateOverview = GenerateOverview,
            GenerateGenres = GenerateGenres,
            GenerateTags = GenerateTags,
            GenerateSortTitle = GenerateSortTitle,
            GenerateContentRating = GenerateContentRating,
            GenerateCommunityRating = GenerateCommunityRating,
            UseAiForCommunityRating = UseAiForCommunityRating,
            OpenReviewAfterGeneration = OpenReviewAfterGeneration,
            MaximumConcurrentGenerations = MaximumConcurrentGenerations,
            OverviewUpdateMode = OverviewUpdateMode,
            GenreUpdateMode = GenreUpdateMode,
            TagUpdateMode = TagUpdateMode,
            SortTitleUpdateMode = SortTitleUpdateMode,
            ContentRatingUpdateMode = ContentRatingUpdateMode,
            CommunityRatingUpdateMode = CommunityRatingUpdateMode,
            OverviewStyle = OverviewStyle,
            Language = Language,
            AllowSpoilers = AllowSpoilers,
            MaximumOverviewCharacters = MaximumOverviewCharacters,
            MaximumGenres = MaximumGenres,
            MaximumTags = MaximumTags,
            MaximumContextItems = MaximumContextItems,
            RequestTimeoutSeconds = RequestTimeoutSeconds,
            CompletedProposalRetentionDays = CompletedProposalRetentionDays,
            MaximumCompletedProposalHistory = MaximumCompletedProposalHistory,
            GenreAllowList = GenreAllowList,
            CustomInstructions = CustomInstructions
        };
    }
}

