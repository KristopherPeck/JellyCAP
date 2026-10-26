namespace Jellyfin.Plugin.JellyAnnotator.Configuration;

/// <summary>
/// AI provider protocols supported by JellyAnnotator.
/// </summary>
public enum AiProviderType
{
    /// <summary>
    /// The OpenAI Responses API.
    /// </summary>
    OpenAi,

    /// <summary>
    /// The Anthropic Messages API.
    /// </summary>
    Anthropic,

    /// <summary>
    /// An OpenAI-compatible Chat Completions API.
    /// </summary>
    OpenAiCompatible
}

