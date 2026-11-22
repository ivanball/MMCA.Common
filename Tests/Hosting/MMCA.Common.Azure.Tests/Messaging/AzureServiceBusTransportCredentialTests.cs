using AwesomeAssertions;
using Azure.Core;
using MassTransit;
using MassTransit.AzureServiceBusTransport.Configuration;
using MassTransit.Transports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Azure.Messaging;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.Azure.Tests.Messaging;

/// <summary>
/// Security lane D2, the token-credential form of the Azure Service Bus transport: a configured fully
/// qualified namespace signs in with a token credential and needs no connection string; a connection
/// string alone keeps the connection-string form; the namespace wins when both are set; neither, or a
/// malformed namespace, fails at <c>AddBrokerMessaging</c> naming what to set. The bus is built and
/// its host settings read, never started, and the credential is a local fake, so nothing here
/// reaches Azure.
/// </summary>
public sealed class AzureServiceBusTransportCredentialTests
{
    private const string NamespaceKey = "MessageBus:AzureServiceBus:FullyQualifiedNamespace";
    private const string ClientIdKey = "MessageBus:AzureServiceBus:ManagedIdentityClientId";
    private const string TokenNamespace = "orders-token.servicebus.windows.net";
    private const string ConnectionStringNamespace = "orders-sas.servicebus.windows.net";

    private const string SasConnectionString =
        "Endpoint=sb://" + ConnectionStringNamespace + "/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=cmVkYWN0ZWQ=";

    private static IConfiguration ConfigurationWith(Dictionary<string, string?> values)
    {
        values["MessageBus:Provider"] = "AzureServiceBus";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceProvider BuildBus(IConfiguration configuration, RecordingCredentialFactory factory)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddBrokerTransport(new AzureServiceBusTransport(factory.Create));
        services.AddBrokerMessaging(configuration);
        return services.BuildServiceProvider();
    }

    private static ServiceBusHostSettings HostSettings(IServiceProvider provider)
    {
        _ = provider.GetRequiredService<IBusControl>();
        var hostConfiguration = provider.GetRequiredService<IBusInstance>().HostConfiguration;
        return hostConfiguration.Should().BeAssignableTo<IServiceBusHostConfiguration>().Subject.Settings;
    }

    [Fact]
    public async Task NamespaceOnly_TakesTheTokenCredentialForm_WithoutAConnectionString()
    {
        var factory = new RecordingCredentialFactory();
        await using var provider = BuildBus(ConfigurationWith(new() { [NamespaceKey] = TokenNamespace }), factory);

        var settings = HostSettings(provider);

        factory.Created.Should().ContainSingle();
        settings.TokenCredential.Should().BeSameAs(factory.Created[0]);
        settings.ServiceUri.Host.Should().Be(TokenNamespace);
        settings.ConnectionString.Should().BeNullOrEmpty("the token form carries no shared access key");
        provider.GetRequiredService<IBusControl>().Address.Host.Should().Be(TokenNamespace);
    }

    [Fact]
    public async Task NamespaceAsAnSbAddress_TakesTheTokenCredentialForm()
    {
        var factory = new RecordingCredentialFactory();
        await using var provider = BuildBus(ConfigurationWith(new() { [NamespaceKey] = $"sb://{TokenNamespace}/" }), factory);

        var settings = HostSettings(provider);

        settings.TokenCredential.Should().BeSameAs(factory.Created.Single());
        settings.ServiceUri.Host.Should().Be(TokenNamespace);
    }

    [Fact]
    public async Task ConnectionStringOnly_TakesTheUnchangedConnectionStringForm()
    {
        var factory = new RecordingCredentialFactory();
        await using var provider = BuildBus(ConfigurationWith(new() { ["MessageBus:ConnectionString"] = SasConnectionString }), factory);

        var settings = HostSettings(provider);

        factory.Created.Should().BeEmpty("no credential is created for the connection-string form");
        settings.TokenCredential.Should().BeNull();
        settings.ConnectionString.Should().Be(SasConnectionString);
        settings.ServiceUri.Host.Should().Be(ConnectionStringNamespace);
    }

    [Fact]
    public async Task NamespaceAndConnectionString_TheNamespaceWins()
    {
        var factory = new RecordingCredentialFactory();
        await using var provider = BuildBus(
            ConfigurationWith(new()
            {
                [NamespaceKey] = TokenNamespace,
                ["MessageBus:ConnectionString"] = SasConnectionString,
            }),
            factory);

        var settings = HostSettings(provider);

        settings.TokenCredential.Should().BeSameAs(factory.Created.Single());
        settings.ServiceUri.Host.Should().Be(TokenNamespace);
        settings.ConnectionString.Should().BeNullOrEmpty("the connection string is ignored when a namespace is set");
        provider.GetRequiredService<IBusControl>().Address.Host.Should().Be(TokenNamespace);
    }

    [Fact]
    public void Neither_FailsAtStartup_NamingBothOptions()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddBrokerTransport(new AzureServiceBusTransport(new RecordingCredentialFactory().Create));

        var act = () => services.AddBrokerMessaging(ConfigurationWith([]));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{NamespaceKey}*")
            .WithMessage("*MessageBus:ConnectionString*");
    }

    [Theory]
    [InlineData("not a namespace")]
    [InlineData("https://orders.servicebus.windows.net/")]
    [InlineData("sb://")]
    public void MalformedNamespace_FailsNamingTheKey(string value)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddBrokerTransport(new AzureServiceBusTransport(new RecordingCredentialFactory().Create));

        var act = () => services.AddBrokerMessaging(ConfigurationWith(new()
        {
            [NamespaceKey] = value,
            ["MessageBus:ConnectionString"] = SasConnectionString,
        }));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{NamespaceKey}*");
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    [InlineData(null)]
    public async Task ManagedIdentityClientId_FromConfiguration_ReachesTheCredentialFactory(string? clientId)
    {
        var factory = new RecordingCredentialFactory();
        await using var provider = BuildBus(
            ConfigurationWith(new() { [NamespaceKey] = TokenNamespace, [ClientIdKey] = clientId }),
            factory);

        _ = HostSettings(provider);

        factory.ClientIds.Should().ContainSingle().Which.Should().Be(clientId);
    }

    [Fact]
    public void CredentialFactory_Null_Throws()
    {
        var act = () => new AzureServiceBusTransport(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Records each client id it is asked for and the credential it returned.</summary>
    private sealed class RecordingCredentialFactory
    {
        public List<string?> ClientIds { get; } = [];

        public List<TokenCredential> Created { get; } = [];

        public NeverCalledCredential Create(string? clientId)
        {
            var credential = new NeverCalledCredential();
            ClientIds.Add(clientId);
            Created.Add(credential);
            return credential;
        }
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
