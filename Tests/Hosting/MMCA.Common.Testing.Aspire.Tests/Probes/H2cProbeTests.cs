using System.Net;
using AwesomeAssertions;
using MMCA.Common.Testing.Aspire.Probes;

namespace MMCA.Common.Testing.Aspire.Tests.Probes;

/// <summary>
/// The version pair is the whole assertion the h2c probe makes, and it is the one thing that can be
/// proven without a server.
/// </summary>
public sealed class H2cProbeTests
{
    private static readonly Uri Endpoint = new("http://localhost:5000/alive");

    [Fact]
    public void CreateRequest_PinsHttp2()
    {
        using var request = H2cProbe.CreateRequest(Endpoint);

        request.Version.Should().Be(HttpVersion.Version20);
    }

    [Fact]
    public void CreateRequest_RefusesToDowngrade()
    {
        using var request = H2cProbe.CreateRequest(Endpoint);

        request.VersionPolicy.Should().Be(
            HttpVersionPolicy.RequestVersionExact,
            "a client allowed to fall back to HTTP/1.1 proves nothing about an h2c listener");
    }

    [Fact]
    public void CreateRequest_IsAGet()
    {
        using var request = H2cProbe.CreateRequest(Endpoint);

        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri.Should().Be(Endpoint);
    }

    [Fact]
    public void CreateRequest_RejectsANullUri()
    {
        var act = () => H2cProbe.CreateRequest(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DefaultTimeout_IsShortEnoughToFailRatherThanWait() =>
        H2cProbe.DefaultTimeout.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
}
