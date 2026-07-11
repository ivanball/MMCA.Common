using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace MMCA.Common.Testing;

/// <summary>
/// Proves the API-versioning machinery works beyond a single version (rubric §9): the
/// <c>/ServiceInfo</c> route is served by both v1.0 (deprecated) and v2.0, selected by the
/// <c>api-version</c> header, and the service reports supported/deprecated versions in response
/// headers (<c>ReportApiVersions</c>). Without a second working version this would all be
/// untestable, so this is the fitness test that keeps the versioning machinery exercised rather
/// than asserted. The <c>ServiceInfo</c> controller ships in <c>MMCA.Common.API</c>
/// (<c>ServiceInfoControllerBase</c>), so the whole body is identical across repos: subclasses
/// supply only their fixture.
/// </summary>
/// <typeparam name="TFixture">The concrete fixture type implementing <see cref="IIntegrationTestFixture"/>.</typeparam>
public abstract class ServiceInfoVersioningContractTestsBase<TFixture> : IntegrationTestBase<TFixture>
    where TFixture : IIntegrationTestFixture
{
    protected ServiceInfoVersioningContractTestsBase(TFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task ServiceInfo_V1_ReturnsMinimalShape_AndIsReportedDeprecated()
    {
        using HttpResponseMessage response = await GetServiceInfoAsync("1.0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("apiVersion").GetString().Should().Be("1.0");
        doc.RootElement.TryGetProperty("supportedVersions", out _)
            .Should().BeFalse("the v1.0 shape does not carry the evolved version lists");

        response.Headers.TryGetValues("api-deprecated-versions", out var deprecated)
            .Should().BeTrue("v1.0 is declared deprecated and ReportApiVersions surfaces it");
        deprecated!.Should().Contain(v => v.Contains("1.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServiceInfo_V2_ReturnsEvolvedShape_AndIsReportedSupported()
    {
        using HttpResponseMessage response = await GetServiceInfoAsync("2.0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("apiVersion").GetString().Should().Be("2.0");
        doc.RootElement.GetProperty("supportedVersions").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("2.0");

        response.Headers.TryGetValues("api-supported-versions", out var supported)
            .Should().BeTrue("the endpoint advertises its supported versions");
        supported!.Should().Contain(v => v.Contains("2.0", StringComparison.Ordinal));
    }

    private async Task<HttpResponseMessage> GetServiceInfoAsync(string apiVersion)
    {
        ClearAuthentication();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ServiceInfo");
        request.Headers.Add("api-version", apiVersion);
        return await Client.SendAsync(request);
    }
}
