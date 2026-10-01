using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.DbContexts;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DbContexts;

/// <summary>
/// Pins when <see cref="CosmosDbContext"/> disables TLS certificate validation. The emulator key
/// alone is not proof of the emulator: it is public, so a connection string carrying it against a
/// real endpoint must still validate the server certificate. Only the emulator key on a loopback
/// endpoint qualifies.
/// </summary>
public sealed class CosmosDbContextTests
{
    // The Cosmos DB Emulator's well-known default account key (published by Microsoft).
    private const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private const string AccountKey = "bm90LXRoZS1lbXVsYXRvci1rZXktanVzdC1hLXRlc3QtdmFsdWU=";

    [Theory]
    [InlineData("https://localhost:8081/")]
    [InlineData("https://127.0.0.1:8081/")]
    [InlineData("https://[::1]:8081/")]
    public void EmulatorKeyOnALoopbackEndpoint_BypassesCertificateValidation(string endpoint) =>
        CosmosDbContext.ShouldBypassCertificateValidation(ConnectionString(endpoint, EmulatorKey))
            .Should().BeTrue("the local emulator serves a self-signed certificate");

    [Theory]
    [InlineData("https://prod.documents.azure.com:443/")]
    [InlineData("https://localhost.attacker.example:443/")]
    public void EmulatorKeyOnANonLoopbackEndpoint_KeepsCertificateValidation(string endpoint) =>
        CosmosDbContext.ShouldBypassCertificateValidation(ConnectionString(endpoint, EmulatorKey))
            .Should().BeFalse("the emulator key is public, so it never proves the endpoint is the local emulator");

    [Fact]
    public void AccountKeyOnALoopbackEndpoint_KeepsCertificateValidation() =>
        CosmosDbContext.ShouldBypassCertificateValidation(ConnectionString("https://localhost:8081/", AccountKey))
            .Should().BeFalse("only the emulator key on a loopback endpoint qualifies");

    private static string ConnectionString(string endpoint, string key) =>
        $"AccountEndpoint={endpoint};AccountKey={key};";
}
