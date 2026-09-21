using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.AI.Tests;

/// <summary>
/// The settings are the whole governance contract, so the tests that matter are the ones about when
/// the contract is INCOMPLETE: a host that switches the dependency on without naming a provider, a
/// model or a key must fail at startup, not on the first request that reaches a user.
/// </summary>
public sealed class AiSettingsTests
{
    [Fact]
    public void Defaults_AreOffAndBounded()
    {
        var settings = new AiSettings();

        settings.Enabled.Should().BeFalse();
        settings.Provider.Should().BeNull("no vendor is the default: the host names the provider it registered");
        settings.Endpoint.Should().BeNull();
        settings.MaxOutputTokens.Should().Be(1024);
        settings.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        settings.AllowTools.Should().BeFalse();
        settings.EnableCache.Should().BeFalse();
        settings.PerCallInputTokenBudget.Should().BeNull();
    }

    [Fact]
    public void Disabled_NeedsNothingElse() => Validate(new AiSettings()).Should().BeEmpty();

    [Fact]
    public void Enabled_RequiresAProvider()
    {
        var results = Validate(new AiSettings { Enabled = true, Model = "claude-haiku-4-5", ApiKey = "key" });

        results.Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(AiSettings.Provider));
    }

    [Fact]
    public void Enabled_RequiresAModel()
    {
        var results = Validate(new AiSettings { Enabled = true, Provider = "Anthropic", ApiKey = "key" });

        results.Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(AiSettings.Model));
    }

    [Fact]
    public void Enabled_RequiresAnApiKey()
    {
        var results = Validate(new AiSettings { Enabled = true, Provider = "Anthropic", Model = "claude-haiku-4-5" });

        results.Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(AiSettings.ApiKey));
    }

    [Fact]
    public void Enabled_RejectsANonPositiveTimeout()
    {
        var results = Validate(new AiSettings
        {
            Enabled = true,
            Provider = "Anthropic",
            Model = "claude-haiku-4-5",
            ApiKey = "key",
            Timeout = TimeSpan.Zero,
        });

        results.Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(AiSettings.Timeout));
    }

    [Fact]
    public void Enabled_RejectsARelativeEndpoint()
    {
        var results = Validate(new AiSettings
        {
            Enabled = true,
            Provider = "OpenAI",
            Model = "gpt-5",
            ApiKey = "key",
            Endpoint = new Uri("v1/", UriKind.Relative),
        });

        results.Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(AiSettings.Endpoint));
    }

    [Fact]
    public void Enabled_WithProviderModelAndKey_IsValid()
    {
        var settings = new AiSettings { Enabled = true, Provider = "OpenAI", Model = "gpt-5", ApiKey = "key" };

        Validate(settings).Should().BeEmpty();
    }

    [Fact]
    public void Enabled_WithAnAbsoluteEndpoint_IsValid()
    {
        var settings = new AiSettings
        {
            Enabled = true,
            Provider = "OpenAI",
            Model = "gpt-5",
            ApiKey = "key",
            Endpoint = new Uri("https://gateway.example.com/openai/v1/"),
        };

        Validate(settings).Should().BeEmpty();
    }

    [Fact]
    public void MaxOutputTokens_MustBePositive()
    {
        var settings = new AiSettings { MaxOutputTokens = 0 };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(settings, new ValidationContext(settings), results, validateAllProperties: true)
            .Should().BeFalse();
        results.Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(AiSettings.MaxOutputTokens));
    }

    private static IReadOnlyList<ValidationResult> Validate(AiSettings settings) =>
        [.. settings.Validate(new ValidationContext(settings))];
}
