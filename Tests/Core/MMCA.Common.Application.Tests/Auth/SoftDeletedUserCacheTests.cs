using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Interfaces;
using Moq;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// Verifies the shared soft-deleted user marker contract: the key shape the API middleware
/// reads, its culture invariance, and the write helper a delete handler calls.
/// </summary>
public sealed class SoftDeletedUserCacheTests
{
    // JwtSettings.AccessTokenExpirationMinutes defaults to 15, and the framework sets no JWT bearer
    // ClockSkew, so the library default (TokenValidationParameters.DefaultClockSkew, 5 minutes) applies.
    private static readonly TimeSpan DefaultAccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DefaultJwtClockSkew = TimeSpan.FromMinutes(5);

    // ── Key shape ──
    [Fact]
    public void KeyFor_ReturnsTheExpectedKeyShape() =>
        SoftDeletedUserCache.KeyFor(42).Should().Be("user:deleted:42");

    [Fact]
    public void KeyFor_IsIdenticalUnderANonInvariantCurrentCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var expectedPositive = SoftDeletedUserCache.KeyFor(1234567);
        var expectedNegative = SoftDeletedUserCache.KeyFor(-42);

        try
        {
            CultureInfo.CurrentCulture = CreateNonInvariantCulture();

            SoftDeletedUserCache.KeyFor(1234567).Should().Be(expectedPositive).And.Be("user:deleted:1234567");
            SoftDeletedUserCache.KeyFor(-42).Should().Be(expectedNegative).And.Be("user:deleted:-42");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    // ── Marker duration ──
    [Fact]
    public void MarkerDuration_OutlivesTheDefaultAccessTokenLifetimePlusTheDefaultClockSkew() =>
        SoftDeletedUserCache.MarkerDuration.Should().BeGreaterThanOrEqualTo(
            DefaultAccessTokenLifetime + DefaultJwtClockSkew,
            "a host without the validator query relies on the marker until an already-issued access token stops validating, which is its expiry plus the clock skew");

    [Fact]
    public void MarkerDurationFor_ARaisedLifetime_CoversItPlusTheDefaultSkew() =>
        SoftDeletedUserCache.MarkerDurationFor(TimeSpan.FromMinutes(60)).Should().Be(
            TimeSpan.FromMinutes(65),
            "a host with a 60-minute token accepts it for 65 minutes under the default bearer skew");

    [Fact]
    public void MarkerDurationFor_ACustomSkew_IsUsed() =>
        SoftDeletedUserCache.MarkerDurationFor(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(2))
            .Should().Be(TimeSpan.FromMinutes(62));

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    public void MarkerDurationFor_NeverGoesBelowTheFloor(int lifetimeMinutes) =>
        SoftDeletedUserCache.MarkerDurationFor(TimeSpan.FromMinutes(lifetimeMinutes)).Should().Be(SoftDeletedUserCache.MarkerDuration);

    // ── Write helper ──
    [Fact]
    public async Task MarkDeletedAsync_WritesTheTrueMarkerUnderTheKeyForTwentyMinutes()
    {
        var cache = new Mock<ICacheService>();

        await SoftDeletedUserCache.MarkDeletedAsync(cache.Object, 7);

        cache.Verify(
            c => c.SetAsync(
                "user:deleted:7",
                true,
                TimeSpan.FromMinutes(20),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MarkDeletedAsync_ForwardsTheCancellationToken()
    {
        var cache = new Mock<ICacheService>();
        using var cts = new CancellationTokenSource();

        await SoftDeletedUserCache.MarkDeletedAsync(cache.Object, 7, cts.Token);

        cache.Verify(
            c => c.SetAsync(
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<TimeSpan?>(),
                cts.Token),
            Times.Once);
    }

    [Fact]
    public async Task MarkDeletedForAsync_WritesTheMarkerForTheGivenDuration()
    {
        var cache = new Mock<ICacheService>();

        await SoftDeletedUserCache.MarkDeletedForAsync(cache.Object, 7, TimeSpan.FromMinutes(65));

        cache.Verify(c => c.SetAsync("user:deleted:7", true, TimeSpan.FromMinutes(65), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkDeletedAsync_NullCache_Throws()
    {
        var act = async () => await SoftDeletedUserCache.MarkDeletedAsync(null!, 7);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Helpers ──

    /// <summary>
    /// Builds a culture whose number formatting differs from the invariant culture, so a
    /// culture-sensitive key would visibly change shape under it.
    /// </summary>
    private static CultureInfo CreateNonInvariantCulture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
        culture.NumberFormat.NegativeSign = "MINUS";
        culture.NumberFormat.NumberGroupSeparator = "_";
        return culture;
    }
}
