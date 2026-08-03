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
    public void MarkerDuration_IsThirtySeconds() =>
        SoftDeletedUserCache.MarkerDuration.Should().Be(TimeSpan.FromSeconds(30));

    // ── Write helper ──
    [Fact]
    public async Task MarkDeletedAsync_WritesTheTrueMarkerUnderTheKeyForThirtySeconds()
    {
        var cache = new Mock<ICacheService>();

        await SoftDeletedUserCache.MarkDeletedAsync(cache.Object, 7);

        cache.Verify(
            c => c.SetAsync(
                "user:deleted:7",
                true,
                TimeSpan.FromSeconds(30),
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
