using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Grpc.Authentication;
using Xunit;

namespace MMCA.Common.Grpc.Tests.Authentication;

/// <summary>
/// The service scheme must never end up as the scheme a plain <c>[Authorize]</c> authenticates
/// with: ASP.NET Core promotes a lone registered scheme to the default, and an explicit default of
/// "ServiceToken" does the same, so either one fails the host at startup.
/// </summary>
public sealed class ServiceTokenDefaultSchemeGuardTests
{
    [Fact]
    public async Task ServiceTokenAsTheOnlyScheme_FailsAtStartup()
    {
        var services = Services();

        await using var provider = services.BuildServiceProvider();

        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();
        validate.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain(ServiceTokenDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task ServiceTokenAsTheExplicitDefaultScheme_FailsAtStartup()
    {
        var services = Services();
        services.AddAuthentication(ServiceTokenDefaults.AuthenticationScheme).AddJwtBearer();

        await using var provider = services.BuildServiceProvider();

        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();
        validate.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task ServiceTokenNextToTheUserDefaultScheme_Starts()
    {
        var services = Services();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        await using var provider = services.BuildServiceProvider();

        var validate = () => provider.GetRequiredService<IStartupValidator>().Validate();
        validate.Should().NotThrow();
    }

    private static ServiceCollection Services()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(ServiceTokenTestData.ConfigurationFor(ServiceTokenTestData.NewBase64Key()))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServiceTokenAuthentication(configuration);
        return services;
    }
}
