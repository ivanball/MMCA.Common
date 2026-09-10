using System.Net;
using Aspire.Hosting.Testing;
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

        // The negative control that makes the assertion above load-bearing: AssertH2cAsync would be
        // vacuous if the endpoint also served HTTP/1.1.
        //
        // The client is built here rather than taken from CreateHttpClient on purpose. Aspire's
        // testing client is not pinned to a version, so it happily speaks HTTP/2 to this endpoint and
        // would prove nothing about the endpoint at all (the first run of this test asserted exactly
        // that and was wrong). RequestVersionExact on 1.1 forbids the upgrade, so the request really
        // is an HTTP/1.1 one, which an Http2-only cleartext listener refuses. That refusal is why the
        // framework ships WithH2cHealthCheck instead of letting Aspire's stock HTTP probe, which does
        // send HTTP/1.1, gate such a resource.
        var endpoint = Application.GetEndpoint(SampleAppHostFixture.H2cServiceResourceName, "http");

        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, AppHostProbePaths.Alive))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        var act = async () => await client.SendAsync(request, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<HttpRequestException>(
            "an Http2-only cleartext endpoint must reject a real HTTP/1.1 request");
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
