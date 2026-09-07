using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using MMCA.Common.API.RateLimiting;
using MMCA.Common.API.Startup;
using MMCA.Common.API.Startup.Pipeline;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// SEC-Common-44 and SEC-ADC-25: the rate-limit and HTTPS-redirect exemptions must key on something
/// the caller cannot forge (the routed endpoint, the negotiated protocol), and anonymous traffic to
/// a real-time hub path must be metered rather than exempt.
/// </summary>
public sealed class RateLimitEdgeCaseSecurityTests
{
    /// <summary>Stands in for the metadata Grpc.AspNetCore.Server attaches to a mapped gRPC method.</summary>
    private sealed class FakeGrpcMetadata;

    private static DefaultHttpContext Ctx(
        string path = "/api/events",
        string? contentType = null,
        bool authenticated = false,
        string? ip = null,
        bool isHttps = false,
        string protocol = "HTTP/1.1",
        Endpoint? endpoint = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.IsHttps = isHttps;
        context.Request.Protocol = protocol;

        if (contentType is not null)
        {
            context.Request.ContentType = contentType;
        }

        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "alice")],
                authenticationType: "TestAuth"));
        }

        if (ip is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        }

        if (endpoint is not null)
        {
            context.Features.Set<IEndpointFeature>(new EndpointFeatureStub(endpoint));
        }

        return context;
    }

    private sealed class EndpointFeatureStub(Endpoint endpoint) : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; } = endpoint;
    }

    private static Endpoint EndpointWith(params object[] metadata)
        => new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test");

    // ── SEC-Common-44: gRPC exemption keys on endpoint metadata ──
    [Fact]
    public void IsGrpcEndpoint_WithNoRoutedEndpoint_ReturnsFalse()
        => WebApplicationBuilderExtensions.IsGrpcEndpoint(Ctx(contentType: "application/grpc")).Should().BeFalse();

    [Fact]
    public void IsGrpcEndpoint_WithUnrelatedEndpointMetadata_ReturnsFalse()
        => WebApplicationBuilderExtensions
            .IsGrpcEndpoint(Ctx(endpoint: EndpointWith(new FakeGrpcMetadata())))
            .Should().BeFalse();

    [Fact]
    public void GlobalRateLimitPartition_ForAForgedGrpcHeader_StillLimitsTheAuthenticatedCaller()
    {
        var partition = WebApplicationBuilderExtensions.GlobalRateLimitPartition(
            Ctx(contentType: "application/grpc", authenticated: true),
            new RateLimitingSettings());

        partition.PartitionKey.Should().Be("alice");
    }

    // ── SEC-Common-44: HTTPS redirect keys on the negotiated protocol ──
    [Theory]
    [InlineData("HTTP/1.1", false, false)]
    [InlineData("HTTP/2", true, false)]
    [InlineData("HTTP/2", false, true)]
    public void IsCleartextHttp2_OnlyMatchesH2C(string protocol, bool isHttps, bool expected)
        => MiddlewarePipelineBuilder
            .IsCleartextHttp2(Ctx(protocol: protocol, isHttps: isHttps))
            .Should().Be(expected);

    [Fact]
    public void IsCleartextHttp2_IgnoresAForgedContentType()
        => MiddlewarePipelineBuilder
            .IsCleartextHttp2(Ctx(contentType: "application/grpc", protocol: "HTTP/1.1"))
            .Should().BeFalse();

    // ── SEC-ADC-25: anonymous hub traffic is metered ──
    [Fact]
    public void GlobalRateLimitPartition_ForAnonymousHubNegotiate_UsesAPerIpPartition()
    {
        var partition = WebApplicationBuilderExtensions.GlobalRateLimitPartition(
            Ctx(path: "/hubs/notifications/negotiate", ip: "203.0.113.9"),
            new RateLimitingSettings());

        partition.PartitionKey.Should().Be("203.0.113.9");
    }

    [Fact]
    public void GlobalRateLimitPartition_ForOtherAnonymousTraffic_StaysExempt()
    {
        var partition = WebApplicationBuilderExtensions.GlobalRateLimitPartition(
            Ctx(path: "/api/events", ip: "203.0.113.9"),
            new RateLimitingSettings());

        partition.PartitionKey.Should().Be("__anonymous");
    }

    [Fact]
    public void GlobalRateLimitPartition_ForAuthenticatedHubTraffic_StillPartitionsPerUser()
    {
        var partition = WebApplicationBuilderExtensions.GlobalRateLimitPartition(
            Ctx(path: "/hubs/notifications", authenticated: true, ip: "203.0.113.9"),
            new RateLimitingSettings());

        partition.PartitionKey.Should().Be("alice");
    }

    [Fact]
    public void HubPathPrefixes_AreConfigurable()
    {
        var settings = new RateLimitingSettings { HubPathPrefixes = ["/realtime"] };

        WebApplicationBuilderExtensions
            .IsAnonymousHubRequest(Ctx(path: "/realtime/negotiate"), settings)
            .Should().BeTrue();
        WebApplicationBuilderExtensions
            .IsAnonymousHubRequest(Ctx(path: "/hubs/notifications"), settings)
            .Should().BeFalse();
    }

    [Fact]
    public void HubPathPrefixes_MatchWholeSegmentsOnly()
        => WebApplicationBuilderExtensions
            .IsAnonymousHubRequest(Ctx(path: "/hubsomething"), new RateLimitingSettings())
            .Should().BeFalse();
}
