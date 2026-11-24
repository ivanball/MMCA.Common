using AwesomeAssertions;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Azure.Messaging;

namespace MMCA.Common.Azure.Tests.Messaging;

/// <summary>
/// Security lane D2, the token-credential form of the Azure Service Bus transport: a configured fully
/// qualified namespace resolves to an <c>sb://</c> address, a malformed one fails naming the key, and
/// the managed identity client id reaches the credential factory. The credential is a local fake, so
/// nothing here reaches Azure.
/// </summary>
public sealed class AzureServiceBusTransportCredentialTests
{
    private const string NamespaceKey = "MessageBus:AzureServiceBus:FullyQualifiedNamespace";
    private const string ClientIdKey = "MessageBus:AzureServiceBus:ManagedIdentityClientId";
    private const string TokenNamespace = "orders-token.servicebus.windows.net";

    [Theory]
    [InlineData(TokenNamespace)]
    [InlineData("sb://" + TokenNamespace + "/")]
    [InlineData("  " + TokenNamespace + "/  ")]
    public void ResolveNamespaceAddress_AcceptsAHostNameOrAnSbAddress(string value) =>
        AzureServiceBusTransport.ResolveNamespaceAddress(value).Should().Be(new Uri($"sb://{TokenNamespace}/"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveNamespaceAddress_Blank_IsNull(string? value) =>
        AzureServiceBusTransport.ResolveNamespaceAddress(value).Should().BeNull();

    [Theory]
    [InlineData("not a namespace")]
    [InlineData("https://orders.servicebus.windows.net/")]
    [InlineData("sb://")]
    public void MalformedNamespace_FailsNamingTheKey(string value)
    {
        var act = () => AzureServiceBusTransport.ResolveNamespaceAddress(value);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{NamespaceKey}*");
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    [InlineData(null)]
    public void ManagedIdentityClientId_FromConfiguration_ReachesTheCredentialFactory(string? clientId)
    {
        var factory = new RecordingCredentialFactory();
        var transport = new AzureServiceBusTransport(factory.Create);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [NamespaceKey] = TokenNamespace, [ClientIdKey] = clientId })
            .Build();

        var settings = AzureServiceBusTransport.ReadSettings(configuration);
        var credential = transport.CreateCredential(settings.ManagedIdentityClientId);

        settings.FullyQualifiedNamespace.Should().Be(TokenNamespace);
        factory.ClientIds.Should().ContainSingle().Which.Should().Be(clientId);
        factory.Created.Should().ContainSingle().Which.Should().BeSameAs(credential);
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
