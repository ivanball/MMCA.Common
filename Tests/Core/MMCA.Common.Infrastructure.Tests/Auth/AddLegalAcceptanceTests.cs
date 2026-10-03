using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Legal;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Pins the DI surface of the opt-in Terms of Service acceptance: <c>AddLegalAcceptance</c> binds
/// <c>Legal:CurrentTermsVersion</c>, and an absent section leaves it unset (the off switch).
/// </summary>
public sealed class AddLegalAcceptanceTests
{
    [Fact]
    public void AddLegalAcceptance_BindsTheCurrentTermsVersionFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Legal:CurrentTermsVersion"] = "2026-10-01",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddLegalAcceptance(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<LegalAcceptanceOptions>>().Value.CurrentTermsVersion
            .Should().Be("2026-10-01");
    }

    [Fact]
    public void AddLegalAcceptance_WithNoLegalSection_LeavesTheVersionUnset()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddLegalAcceptance(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<LegalAcceptanceOptions>>().Value.CurrentTermsVersion
            .Should().BeNull("a host that never configures the section keeps the feature off");
    }
}
