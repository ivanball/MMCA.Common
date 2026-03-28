using AwesomeAssertions;
using MMCA.Common.Infrastructure.Caching;

namespace MMCA.Common.Infrastructure.Tests.Caching;

public class CacheOptionsTests
{
    // ── DefaultExpiration ──
    [Fact]
    public void DefaultExpiration_Returns30Seconds() =>
        CacheOptions.DefaultExpiration.AbsoluteExpirationRelativeToNow
            .Should().Be(TimeSpan.FromSeconds(30));

    // ── Create ──
    [Fact]
    public void Create_WithExpiration_ReturnsCustomExpiration()
    {
        var expiration = TimeSpan.FromMinutes(5);
        var options = CacheOptions.Create(expiration);
        options.AbsoluteExpirationRelativeToNow.Should().Be(expiration);
    }

    [Fact]
    public void Create_WithNull_ReturnsDefaultExpiration()
    {
        var options = CacheOptions.Create(null);
        options.AbsoluteExpirationRelativeToNow
            .Should().Be(TimeSpan.FromSeconds(30));
    }
}
