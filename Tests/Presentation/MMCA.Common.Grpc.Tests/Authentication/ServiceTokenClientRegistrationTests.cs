using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Grpc.Authentication;
using Xunit;

namespace MMCA.Common.Grpc.Tests.Authentication;

/// <summary>
/// Registration-level assertions for <c>AddServiceTokenClient(configuration)</c>: it binds the
/// <c>Grpc:ServiceToken</c> section, refuses an invalid one through options validation, registers
/// <see cref="IServiceTokenProvider"/> as a singleton (so its token cache is process-wide), and only
/// TryAdds <see cref="TimeProvider.System"/> so a host's own clock is kept.
/// </summary>
public sealed class ServiceTokenClientRegistrationTests
{
    [Fact]
    public async Task AddServiceTokenClient_BindsTheSection()
    {
        var key = ServiceTokenTestData.NewBase64Key();
        var values = ServiceTokenTestData.ConfigurationFor(key);
        values[$"{ServiceTokenTestData.SectionName}:Issuer"] = "custom-issuer";
        values[$"{ServiceTokenTestData.SectionName}:Audience"] = "custom-audience";
        values[$"{ServiceTokenTestData.SectionName}:LifetimeMinutes"] = "10";
        await using var provider = BuildProvider(values);

        var settings = provider.GetRequiredService<IOptions<ServiceTokenSettings>>().Value;

        settings.SigningKey.Should().Be(key);
        settings.ServiceName.Should().Be(ServiceTokenTestData.ServiceName);
        settings.Issuer.Should().Be("custom-issuer");
        settings.Audience.Should().Be("custom-audience");
        settings.LifetimeMinutes.Should().Be(10);
    }

    [Fact]
    public async Task AddServiceTokenClient_RegistersTheProviderAsASingleton()
    {
        var services = new ServiceCollection();
        services.AddServiceTokenClient(Configuration(ServiceTokenTestData.ConfigurationFor(ServiceTokenTestData.NewBase64Key())));

        services.Should().Contain(d =>
            d.ServiceType == typeof(IServiceTokenProvider)
            && d.Lifetime == ServiceLifetime.Singleton);

        await using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<IServiceTokenProvider>();
        var second = provider.GetRequiredService<IServiceTokenProvider>();

        first.Should().BeSameAs(second);
        first.Should().BeOfType<ServiceTokenProvider>();
        first.GetToken().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AddServiceTokenClient_RegistersTheSystemTimeProviderWhenNoneIsPresent()
    {
        await using var provider = BuildProvider(ServiceTokenTestData.ConfigurationFor(ServiceTokenTestData.NewBase64Key()));

        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public async Task AddServiceTokenClient_KeepsAnAlreadyRegisteredTimeProvider()
    {
        var clock = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddServiceTokenClient(Configuration(ServiceTokenTestData.ConfigurationFor(ServiceTokenTestData.NewBase64Key())));

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(clock);
    }

    [Fact]
    public async Task AddServiceTokenClient_WithAnInvalidSection_FailsOptionsValidationWhenResolved()
    {
        var values = ServiceTokenTestData.ConfigurationFor(ServiceTokenTestData.NewBase64Key(byteCount: 16));
        await using var provider = BuildProvider(values);

        var act = () => provider.GetRequiredService<IOptions<ServiceTokenSettings>>().Value;

        act.Should().Throw<OptionsValidationException>(
            "a key too short for HS256 must stop the host, not mint tokens the receiver rejects");
    }

    [Fact]
    public async Task AddServiceTokenClient_WithoutTheSection_FailsOptionsValidationWhenResolved()
    {
        await using var provider = BuildProvider(new Dictionary<string, string?>(StringComparer.Ordinal));

        var act = () => provider.GetRequiredService<IOptions<ServiceTokenSettings>>().Value;

        act.Should().Throw<OptionsValidationException>("a client with no key and no service name cannot mint a token");
    }

    [Fact]
    public async Task AddServiceTokenClient_RegistersValidateOnStart()
    {
        var values = ServiceTokenTestData.ConfigurationFor(string.Empty);
        await using var provider = BuildProvider(values);

        var startupValidator = provider.GetService<IStartupValidator>();

        startupValidator.Should().NotBeNull("ValidateOnStart registers the startup validator the host runs before serving");
        var act = () => startupValidator!.Validate();
        act.Should().Throw<OptionsValidationException>();
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddServiceTokenClient(Configuration(values));
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
