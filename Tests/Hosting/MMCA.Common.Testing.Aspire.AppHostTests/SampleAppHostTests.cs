using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.Aspire.Fixtures;
using MMCA.Common.Testing.Aspire.Probes;

namespace MMCA.Common.Testing.Aspire.AppHostTests;

/// <summary>
/// Proves the shipped fixture against a real, booted AppHost: it starts, it waits for readiness the
/// right way, and every <c>Assert*</c> helper holds against a host wired with the framework's own
/// extensions. This is the layer nothing else in the repo executes.
/// <para>
/// Every test opens with the skip guard. Skipping is the consuming project's call by design, because
/// MMCA.Common.Testing.Aspire deliberately takes no dependency on the xUnit assertion library, and
/// this class is also the worked example of how a consumer writes that line.
/// </para>
/// </summary>
/// <param name="fixture">The collection fixture that owns the running application.</param>
[Collection(SampleAppHostCollection.Name)]
public sealed class SampleAppHostTests(SampleAppHostFixture fixture)
    : AppHostTestBase<SampleAppHostFixture>(fixture)
{
    [Fact]
    public void Fixture_BootsTheAppHost_WhenThePreconditionsAreMet()
    {
        SkipIfUnavailable();

        Application.Should().NotBeNull("the collection fixture starts the AppHost once for the whole tier");
    }

    [Fact]
    public void ConfigureBuilder_ReachesTheRealBuilder_BeforeTheApplicationIsBuilt()
    {
        SkipIfUnavailable();

        Application.Services.GetRequiredService<IConfiguration>()[SampleAppHostFixture.MarkerConfigurationKey]
            .Should().Be(
                SampleAppHostFixture.MarkerConfigurationValue,
                "test-only configuration pushed in the hook must survive into the built application");
    }

    [Fact]
    public async Task Service_AnswersLiveness()
    {
        SkipIfUnavailable();

        await AssertAliveAsync(
            SampleAppHostFixture.ServiceResourceName,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Service_AnswersTheFullHealthReport()
    {
        SkipIfUnavailable();

        await AssertHealthyAsync(
            SampleAppHostFixture.ServiceResourceName,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Service_AnswersReadiness()
    {
        SkipIfUnavailable();

        await AssertReadyAsync(
            SampleAppHostFixture.ServiceResourceName,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Service_PublishesAnRsaKeySet()
    {
        SkipIfUnavailable();

        var document = await AssertJwksAsync(
            SampleAppHostFixture.ServiceResourceName,
            cancellationToken: TestContext.Current.CancellationToken);

        document.Should().Contain(
            "\"kty\"",
            "the key set must be a real JWKS document rather than an empty placeholder");
    }

    /// <summary>
    /// Proves the h2c assertion end to end against a resource whose service runs
    /// <c>HttpProtocols.Http2</c> alone and is gated by the framework's <c>WithH2cHealthCheck()</c>:
    /// the stock HTTP probe speaks HTTP/1.1, so nothing else could have turned it healthy.
    /// <para>
    /// What it does NOT prove, and what no AppHost-tier test can: that the SERVICE refuses HTTP/1.1.
    /// Aspire fronts a project resource with its own endpoint proxy, so the protocol a client
    /// observes here is the proxy's, and the proxy serves HTTP/1.1 happily. That half of the contract
    /// is proven in <c>H2cProbeServerTests</c>, against a listener the test owns.
    /// </para>
    /// </summary>
    /// <returns>A task that completes when the assertion holds.</returns>
    [Fact]
    public async Task Http2OnlyService_NegotiatesH2cOnItsCleartextEndpoint()
    {
        SkipIfUnavailable();

        await AssertH2cAsync(
            SampleAppHostFixture.H2cServiceResourceName,
            AppHostProbePaths.Alive,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Service_CarriesTheLogicalDataSourceRoutingKey()
    {
        SkipIfUnavailable();

        var connectionString = await AssertDataSourceAsync(
            SampleAppHostFixture.ServiceResourceName,
            SampleAppHostFixture.LogicalDataSourceName,
            TestContext.Current.CancellationToken);

        connectionString.Should().Contain(
            "Data Source=",
            "WithSqliteDataSource injects a SQLite connection string for the logical name");
    }

    [Fact]
    public async Task CreateHttpClient_TargetsTheNamedResource()
    {
        SkipIfUnavailable();

        using var client = CreateHttpClient(SampleAppHostFixture.ServiceResourceName);
        using var response = await client.GetAsync(
            new Uri(AppHostProbePaths.Alive, UriKind.Relative),
            TestContext.Current.CancellationToken);

        client.BaseAddress.Should().NotBeNull();
        response.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public void Fixture_MintedAnEphemeralKeypair()
    {
        SkipIfUnavailable();

        Fixture.E2eRsaKeys.Should().NotBeNull(
            "an AppHost that forwards E2E_JWT_* needs key material, and the fixture supplies it when the environment does not");
        Environment.GetEnvironmentVariable("E2E_JWT_PUBLIC_KEY_PEM")
            .Should().NotBeNullOrWhiteSpace("the keys reach the AppHost through this process's own environment");
    }

    /// <summary>
    /// The line a consumer writes at the top of each test: the fixture reports WHY the collection
    /// cannot run, and xUnit records a named skip rather than a failure.
    /// </summary>
    private void SkipIfUnavailable() =>
        Assert.SkipWhen(!Fixture.IsAvailable, Fixture.SkipReason ?? "The AppHost collection is unavailable.");
}
