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
    public async Task Http2OnlyService_RefusesAnOrdinaryHttp11Client()
    {
        SkipIfUnavailable();

        // The negative control that makes the assertion above load-bearing. An Http2-only cleartext
        // endpoint answers a default HttpClient's HTTP/1.1 request with GOAWAY HTTP_1_1_REQUIRED, so
        // this must throw. Two things ride on it: AssertH2cAsync would be vacuous if the endpoint
        // also served HTTP/1.1, and this is exactly why the framework ships WithH2cHealthCheck
        // instead of letting Aspire's stock HTTP probe gate such a resource.
        using var client = CreateHttpClient(SampleAppHostFixture.H2cServiceResourceName);

        var act = async () => await client.GetAsync(
            new Uri(AppHostProbePaths.Alive, UriKind.Relative),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>(
            "an Http2-only cleartext endpoint must reject HTTP/1.1, which is what makes the h2c assertion mean something");
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
