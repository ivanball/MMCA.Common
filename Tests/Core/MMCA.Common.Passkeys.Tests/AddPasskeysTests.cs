using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Passkeys;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.Passkeys.Tests;

/// <summary>
/// <c>AddPasskeys(configuration)</c> (ADR-139): the ADR-116 opt-in shape. Enabled settings are
/// validated at startup and a missing value fails naming its configuration key; disabled settings
/// need nothing else; and a host that never calls it has no passkey service at all.
/// </summary>
public sealed class AddPasskeysTests
{
    private static Dictionary<string, string?> Valid() => new(StringComparer.Ordinal)
    {
        ["Passkeys:Enabled"] = "true",
        ["Passkeys:RelyingPartyId"] = "example.com",
        ["Passkeys:RelyingPartyName"] = "Example",
        ["Passkeys:Origins:0"] = "https://app.example.com",
    };

    [Theory]
    [InlineData("Passkeys:RelyingPartyId")]
    [InlineData("Passkeys:RelyingPartyName")]
    [InlineData("Passkeys:Origins:0")]
    public void Enabled_WithAMissingValue_FailsValidationNamingTheKey(string missingKey)
    {
        var values = Valid();
        values.Remove(missingKey);

        var act = () => Resolve(values).GetRequiredService<IOptions<PasskeySettings>>().Value;

        var expectedKey = missingKey.Replace(":0", string.Empty, StringComparison.Ordinal);
        act.Should().Throw<OptionsValidationException>().WithMessage($"*{expectedKey}*");
    }

    [Fact]
    public void Enabled_WithAnOriginThatIsNotAnAbsoluteOrigin_FailsValidationNamingTheKey()
    {
        var values = Valid();
        values["Passkeys:Origins:0"] = "app.example.com";

        var act = () => Resolve(values).GetRequiredService<IOptions<PasskeySettings>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*Passkeys:Origins*");
    }

    [Fact]
    public void Enabled_WithEveryValue_ResolvesAnEnabledService()
    {
        var provider = Resolve(Valid());

        provider.GetRequiredService<IPasskeyService>().IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Disabled_NeedsNoOtherValue_AndTheServiceReportsDisabled()
    {
        var provider = Resolve(new Dictionary<string, string?>(StringComparer.Ordinal) { ["Passkeys:Enabled"] = "false" });

        provider.GetRequiredService<IOptions<PasskeySettings>>().Value.Enabled.Should().BeFalse();
        provider.GetRequiredService<IPasskeyService>().IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void NeverCalled_RegistersNoPasskeyService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();

        using var provider = services.BuildServiceProvider();

        provider.GetService<IPasskeyService>().Should().BeNull();
        provider.GetService<IPasskeyCredentialStore>().Should().BeNull();
    }

    private static ServiceProvider Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Moq.Mock.Of<IPasskeyCredentialStore>());
        services.AddScoped(_ => Moq.Mock.Of<Application.Auth.IAuthenticationService>());
        services.AddPasskeys(configuration);
        return services.BuildServiceProvider();
    }
}
