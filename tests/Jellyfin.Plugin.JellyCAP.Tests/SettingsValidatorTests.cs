using Jellyfin.Plugin.JellyCAP.Configuration;
using Jellyfin.Plugin.JellyCAP.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.JellyCAP.Tests;

[TestClass]
public sealed class SettingsValidatorTests
{
    [TestMethod]
    public void ValidateTrimsValuesWithoutMutatingSource()
    {
        var source = CreateValidConfiguration();
        source.Endpoint = "  https://example.com/v1  ";
        source.Model = "  test-model  ";
        source.Language = "  English  ";

        var result = SettingsValidator.Validate(source);

        Assert.AreEqual("https://example.com/v1", result.Endpoint);
        Assert.AreEqual("test-model", result.Model);
        Assert.AreEqual("English", result.Language);
        Assert.AreEqual("  https://example.com/v1  ", source.Endpoint);
        Assert.AreEqual("  test-model  ", source.Model);
    }

    [TestMethod]
    public void ValidateClampsBoundedNumericSettings()
    {
        var source = CreateValidConfiguration();
        source.MaximumOverviewCharacters = int.MaxValue;
        source.MaximumGenres = 0;
        source.MaximumTags = int.MaxValue;
        source.MaximumContextItems = 0;
        source.RequestTimeoutSeconds = 1;
        source.MaximumConcurrentGenerations = 99;
        source.ProviderRequestDelaySeconds = -1;
        source.CompletedProposalRetentionDays = int.MaxValue;
        source.MaximumCompletedProposalHistory = int.MaxValue;

        var result = SettingsValidator.Validate(source);

        Assert.AreEqual(10000, result.MaximumOverviewCharacters);
        Assert.AreEqual(1, result.MaximumGenres);
        Assert.AreEqual(100, result.MaximumTags);
        Assert.AreEqual(1, result.MaximumContextItems);
        Assert.AreEqual(10, result.RequestTimeoutSeconds);
        Assert.AreEqual(5, result.MaximumConcurrentGenerations);
        Assert.AreEqual(0, result.ProviderRequestDelaySeconds);
        Assert.AreEqual(3650, result.CompletedProposalRetentionDays);
        Assert.AreEqual(10000, result.MaximumCompletedProposalHistory);
    }

    [TestMethod]
    public void ValidateRejectsMissingModelWhenAiIsEnabled()
    {
        var source = CreateValidConfiguration();
        source.Model = " ";

        Assert.ThrowsExactly<ArgumentException>(() => { SettingsValidator.Validate(source); });
    }

    [TestMethod]
    public void ValidateAllowsMissingModelWhenOnlyLocalMetadataIsEnabled()
    {
        var source = CreateLocalOnlyConfiguration();

        var result = SettingsValidator.Validate(source);

        Assert.AreEqual(string.Empty, result.Model);
        Assert.IsTrue(result.CalculateReleaseMetadata);
    }

    [TestMethod]
    public void ValidateRejectsConfigurationWithoutEnabledFields()
    {
        var source = CreateLocalOnlyConfiguration();
        source.CalculateReleaseMetadata = false;

        Assert.ThrowsExactly<ArgumentException>(() => { SettingsValidator.Validate(source); });
    }

    [TestMethod]
    public void ValidateRequiresEndpointForCompatibleProvider()
    {
        var source = CreateValidConfiguration();
        source.ProviderType = AiProviderType.OpenAiCompatible;
        source.Endpoint = string.Empty;

        Assert.ThrowsExactly<ArgumentException>(() => { SettingsValidator.Validate(source); });
    }

    [TestMethod]
    [DataRow("https://user:credential@example.com/v1")]
    [DataRow("https://example.com/v1?key=value")]
    [DataRow("https://example.com/v1#fragment")]
    [DataRow("file:///tmp/provider")]
    [DataRow("not-a-url")]
    public void ValidateRejectsUnsafeEndpointShapes(string endpoint)
    {
        var source = CreateValidConfiguration();
        source.Endpoint = endpoint;

        Assert.ThrowsExactly<ArgumentException>(() => { SettingsValidator.Validate(source); });
    }

    [TestMethod]
    public void ValidateRejectsNonLoopbackHttpWithoutExplicitPermission()
    {
        var source = CreateValidConfiguration();
        source.Endpoint = "http://example.com/v1";

        Assert.ThrowsExactly<ArgumentException>(() => { SettingsValidator.Validate(source); });
    }

    [TestMethod]
    public void ValidateAllowsLoopbackHttpWithoutInsecurePermission()
    {
        var source = CreateValidConfiguration();
        source.Endpoint = "http://127.0.0.1:11434/v1";

        var result = SettingsValidator.Validate(source);

        Assert.AreEqual(source.Endpoint, result.Endpoint);
        Assert.IsFalse(result.AllowInsecureEndpoint);
    }

    [TestMethod]
    public void ValidateAllowsExplicitlyPermittedNonLoopbackHttp()
    {
        var source = CreateValidConfiguration();
        source.Endpoint = "http://provider.internal/v1";
        source.AllowInsecureEndpoint = true;

        var result = SettingsValidator.Validate(source);

        Assert.IsTrue(result.AllowInsecureEndpoint);
    }

    [TestMethod]
    [DataRow("JELLYCAP_OPENAI_KEY")]
    [DataRow("JELLYCAP_KEY_2")]
    public void ValidateAllowsPluginOwnedEnvironmentVariables(string name)
    {
        var source = CreateValidConfiguration();
        source.ApiKeyEnvironmentVariable = name;

        var result = SettingsValidator.Validate(source);

        Assert.AreEqual(name, result.ApiKeyEnvironmentVariable);
    }

    [TestMethod]
    [DataRow("jellycap_key")]
    [DataRow("JELLYCAP_")]
    [DataRow("JELLYCAP-KEY")]
    [DataRow("PATH")]
    [DataRow("JELLYCAP_KEY=value")]
    public void ValidateRejectsUnapprovedEnvironmentVariables(string name)
    {
        var source = CreateValidConfiguration();
        source.ApiKeyEnvironmentVariable = name;

        Assert.ThrowsExactly<ArgumentException>(() => { SettingsValidator.Validate(source); });
    }

    [TestMethod]
    public void ValidateAllowsMatchingOfficialEnvironmentVariableAtDefaultEndpoint()
    {
        var source = CreateValidConfiguration();
        source.ProviderType = AiProviderType.OpenAi;
        source.Endpoint = string.Empty;
        source.ApiKeyEnvironmentVariable = "OPENAI_API_KEY";

        var result = SettingsValidator.Validate(source);

        Assert.AreEqual("OPENAI_API_KEY", result.ApiKeyEnvironmentVariable);
    }

    [TestMethod]
    public void ValidateRejectsOfficialEnvironmentVariableForCustomEndpoint()
    {
        var source = CreateValidConfiguration();
        source.ProviderType = AiProviderType.OpenAi;
        source.Endpoint = "https://example.com/v1";
        source.ApiKeyEnvironmentVariable = "OPENAI_API_KEY";

        Assert.ThrowsExactly<ArgumentException>(() => { SettingsValidator.Validate(source); });
    }

    [TestMethod]
    public void ValidateRejectsUndefinedConfigurationModes()
    {
        var source = CreateValidConfiguration();
        source.ProviderType = (AiProviderType)999;

        Assert.ThrowsExactly<ArgumentException>(() => { SettingsValidator.Validate(source); });
    }

    [TestMethod]
    public void ValidateRejectsOversizedCustomInstructions()
    {
        var source = CreateValidConfiguration();
        source.CustomInstructions = new string('x', 4001);

        Assert.ThrowsExactly<ArgumentException>(() => { SettingsValidator.Validate(source); });
    }

    private static PluginConfiguration CreateValidConfiguration()
    {
        return new PluginConfiguration
        {
            Model = "test-model"
        };
    }

    private static PluginConfiguration CreateLocalOnlyConfiguration()
    {
        return new PluginConfiguration
        {
            Model = string.Empty,
            GenerateOverview = false,
            GenerateGenres = false,
            GenerateTags = false,
            GenerateSortTitle = false,
            GenerateContentRating = false,
            GenerateCommunityRating = false,
            CalculateReleaseMetadata = true
        };
    }
}
