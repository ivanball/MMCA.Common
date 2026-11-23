using AwesomeAssertions;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Azure.Messaging;
using MMCA.Common.Infrastructure;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;

namespace MMCA.Common.Azure.Tests.Messaging;

/// <summary>
/// The Azure Service Bus transport against the core broker-transport extension point: registered by
/// <c>AddAzureServiceBusTransport()</c>, selected by <c>MessageBus:Provider=AzureServiceBus</c>, and
/// what its <c>Register</c> adds or refuses. Every client is built against a placeholder endpoint, and
/// nothing here opens a connection.
/// </summary>
public sealed class AzureServiceBusTransportTests
{
    private const string NamespaceConnectionString =
        "Endpoint=sb://mmca-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=cmVkYWN0ZWQ=";

    private const string EmulatorConnectionString =
        "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private const string NamespaceKey = "MessageBus:AzureServiceBus:FullyQualifiedNamespace";
    private const string ClientIdKey = "MessageBus:AzureServiceBus:ManagedIdentityClientId";

    private static IConfiguration ConfigurationFor(string provider, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["MessageBus:Provider"] = provider,
            ["MessageBus:ConnectionString"] = NamespaceConnectionString,
            ["applicationName"] = "Azure.Tests.Service",
        };
        foreach (var (key, value) in extra ?? [])
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static BrokerTransportContext ContextFor(string? connectionString, Dictionary<string, string?>? configuration = null, MessageBusSettings? settings = null) =>
        new(
            settings ?? new MessageBusSettings(),
            connectionString,
            new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build(),
            new BrokerTopology("demo", "orders", []));

    [Fact]
    public void Name_IsTheConfiguredProviderValue() =>
        new AzureServiceBusTransport().Name.Should().Be("AzureServiceBus");

    [Fact]
    public void MaxRedeliveryDelay_IsUncapped_BecauseServiceBusSchedulesArbitrarilyFar() =>
        new AzureServiceBusTransport().MaxRedeliveryDelay.Should().BeNull();

    [Fact]
    public void AddAzureServiceBusTransport_RegistersTheTransportOnce()
    {
        var services = new ServiceCollection();

        services.AddAzureServiceBusTransport();
        services.AddAzureServiceBusTransport();

        services.Where(d => d.ServiceType == typeof(IBrokerTransport))
            .Should().ContainSingle()
            .Which.ImplementationInstance.Should().BeOfType<AzureServiceBusTransport>();
    }

    [Fact]
    public async Task AddBrokerMessaging_AfterTheTransport_RegistersItsPublisherAndReceiver()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAzureServiceBusTransport();

        services.AddBrokerMessaging(ConfigurationFor("AzureServiceBus"));

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IBrokerPublisher>().Should().BeOfType<AzureServiceBusPublisher>();
        provider.GetServices<IHostedService>().OfType<AzureServiceBusReceiver>().Should().ContainSingle();
        provider.GetService<AzureServiceBusTopologyProvisioner>().Should().NotBeNull("MessageBus:ProvisionTopology defaults to true");
    }

    [Fact]
    public async Task ProvisionTopologyOff_RegistersNoProvisioner()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAzureServiceBusTransport();

        services.AddBrokerMessaging(ConfigurationFor("AzureServiceBus", new() { ["MessageBus:ProvisionTopology"] = "false" }));

        await using var provider = services.BuildServiceProvider();
        provider.GetService<AzureServiceBusTopologyProvisioner>().Should().BeNull();
        provider.GetRequiredService<IBrokerPublisher>().Should().BeOfType<AzureServiceBusPublisher>();
    }

    [Fact]
    public void AddBrokerMessaging_WithoutTheTransport_NamesThisPackage()
    {
        var act = () => new ServiceCollection().AddBrokerMessaging(ConfigurationFor("AzureServiceBus"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MMCA.Common.Azure*AddAzureServiceBusTransport()*");
    }

    [Fact]
    public void Register_RejectsNullArguments()
    {
        var transport = new AzureServiceBusTransport();

        var noServices = () => transport.Register(null!, ContextFor(NamespaceConnectionString));
        var noContext = () => transport.Register(new ServiceCollection(), null!);

        noServices.Should().Throw<ArgumentNullException>();
        noContext.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Register_WithNeitherNamespaceNorConnectionString_FailsNamingBoth()
    {
        var act = () => new AzureServiceBusTransport().Register(new ServiceCollection(), ContextFor(connectionString: null));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{NamespaceKey}*MessageBus:ConnectionString*");
    }

    [Fact]
    public void Register_WithAMalformedNamespace_FailsNamingTheKey()
    {
        var act = () => new AzureServiceBusTransport().Register(
            new ServiceCollection(),
            ContextFor(NamespaceConnectionString, new() { [NamespaceKey] = "https://orders.servicebus.windows.net/" }));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{NamespaceKey}*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("localhost:5300")]
    [InlineData("ftp://localhost:5300")]
    public void Register_OnTheEmulatorWithoutAValidAdminEndpoint_Fails(string? adminEndpoint)
    {
        var act = () => new AzureServiceBusTransport().Register(
            new ServiceCollection(),
            ContextFor(EmulatorConnectionString, settings: new MessageBusSettings { EmulatorAdminEndpoint = adminEndpoint }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*MessageBus:EmulatorAdminEndpoint*");
    }

    [Fact]
    public void ResolveEndpoint_ANamespace_WinsOverTheConnectionString_AndSignsInWithTheManagedIdentity()
    {
        var requestedClientIds = new List<string?>();
        var transport = new AzureServiceBusTransport(clientId =>
        {
            requestedClientIds.Add(clientId);
            return new NeverCalledCredential();
        });

        var endpoint = transport.ResolveEndpoint(ContextFor(
            NamespaceConnectionString,
            new() { [NamespaceKey] = "sb://orders-token.servicebus.windows.net/", [ClientIdKey] = "11111111-2222-3333-4444-555555555555" }));

        endpoint.FullyQualifiedNamespace.Should().Be("orders-token.servicebus.windows.net");
        endpoint.Credential.Should().BeOfType<NeverCalledCredential>();
        endpoint.ConnectionString.Should().BeNull("the token sign-in wins when a connection string is also present");
        endpoint.IsEmulator.Should().BeFalse();
        requestedClientIds.Should().Equal("11111111-2222-3333-4444-555555555555");

        var client = endpoint.CreateClient();
        client.FullyQualifiedNamespace.Should().Be("orders-token.servicebus.windows.net");
    }

    [Fact]
    public void ResolveEndpoint_AConnectionString_IsUsedForBothPlanes()
    {
        var endpoint = new AzureServiceBusTransport().ResolveEndpoint(ContextFor(NamespaceConnectionString));

        endpoint.FullyQualifiedNamespace.Should().BeNull();
        endpoint.Credential.Should().BeNull();
        endpoint.ConnectionString.Should().Be(NamespaceConnectionString);
        endpoint.AdministrationConnectionString.Should().Be(NamespaceConnectionString);
        endpoint.IsEmulator.Should().BeFalse();
        endpoint.CreateClient().FullyQualifiedNamespace.Should().Be("mmca-test.servicebus.windows.net");
    }

    [Fact]
    public void ResolveEndpoint_AnEmulatorConnectionString_DerivesTheAdminPlaneFromTheAdminEndpoint()
    {
        var endpoint = new AzureServiceBusTransport().ResolveEndpoint(
            ContextFor(EmulatorConnectionString, settings: new MessageBusSettings { EmulatorAdminEndpoint = "http://localhost:5300" }));

        endpoint.IsEmulator.Should().BeTrue();
        endpoint.ConnectionString.Should().Be(EmulatorConnectionString);
        endpoint.AdministrationConnectionString.Should().Be(
            "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");
    }

    /// <summary>A credential that fails the test if anything asks it for a token.</summary>
    private sealed class NeverCalledCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No token may be requested in a unit test.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No token may be requested in a unit test.");
    }
}
