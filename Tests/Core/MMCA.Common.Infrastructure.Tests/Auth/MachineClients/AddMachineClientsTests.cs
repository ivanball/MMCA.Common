using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.MachineClients;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Configuration;
using MMCA.Common.Shared.Auth.Permissions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Auth.MachineClients;

/// <summary>
/// <c>AddMachineClients(configuration)</c> (ADR-140): the ADR-116 opt-in shape. An enabled host without
/// the consumer's <see cref="IServiceAccountProvisioner"/> fails at startup naming it, the token lifetime
/// is held inside the revocation marker's window, an enabled section without the call trips the startup
/// tripwire, and a host that never calls it has no machine-client service at all.
/// </summary>
public sealed class AddMachineClientsTests
{
    [Fact]
    public void Enabled_WithoutAProvisioner_FailsStartupNamingTheRegistration()
    {
        var act = () => Resolve(new() { ["MachineClients:Enabled"] = "true" }, withProvisioner: false)
            .GetRequiredService<IOptions<MachineClientSettings>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*IServiceAccountProvisioner*");
    }

    [Fact]
    public void Enabled_WithAProvisioner_ResolvesAnEnabledService()
    {
        using var provider = Resolve(new() { ["MachineClients:Enabled"] = "true" }, withProvisioner: true);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IMachineClientService>().IsEnabled.Should().BeTrue();
        provider.GetRequiredService<IOptions<MachineClientSettings>>().Value.AccessTokenMinutes.Should().Be(15);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("16")]
    public void AnAccessTokenLifetimeOutsideOneToFifteenMinutes_FailsNamingTheKey(string minutes)
    {
        var act = () => Resolve(new() { ["MachineClients:AccessTokenMinutes"] = minutes }, withProvisioner: true)
            .GetRequiredService<IOptions<MachineClientSettings>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*MachineClients:AccessTokenMinutes*");
    }

    [Fact]
    public void Disabled_NeedsNoProvisioner_AndTheServiceReportsDisabled()
    {
        using var provider = Resolve(new() { ["MachineClients:Enabled"] = "false" }, withProvisioner: false);

        provider.GetRequiredService<IOptions<MachineClientSettings>>().Value.Enabled.Should().BeFalse();
    }

    [Fact]
    public void NeverCalled_RegistersNoMachineClientService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();

        using var provider = services.BuildServiceProvider();

        provider.GetService<IMachineClientService>().Should().BeNull();
        provider.GetService<IMachineClientStore>().Should().BeNull();
    }

    [Fact]
    public void EnabledWithoutTheCall_TripsTheStartupTripwireNamingIt()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["MachineClients:Enabled"] = "true" })
            .Build());
        using var provider = services.BuildServiceProvider();

        var result = new CloudAdapterTripwire(provider).Validate(Options.DefaultName, new CloudAdapterTripwireOptions());

        result.Failed.Should().BeTrue("an enabled section the host cannot serve would answer 404 on every machine-client route");
        result.FailureMessage.Should().Contain("MachineClients:Enabled").And.Contain("AddMachineClients(configuration)");
    }

    private static ServiceProvider Resolve(Dictionary<string, string?> values, bool withProvisioner)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        services.AddSingleton(Mock.Of<ITokenService>());
        services.AddSingleton<IPermissionRegistry>(new PermissionRegistryBuilder().Build());
        services.AddSingleton(Mock.Of<IMachineClientStore>());
        if (withProvisioner)
        {
            services.AddScoped(_ => Mock.Of<IServiceAccountProvisioner>());
        }

        services.AddMachineClients(configuration);
        return services.BuildServiceProvider();
    }
}
