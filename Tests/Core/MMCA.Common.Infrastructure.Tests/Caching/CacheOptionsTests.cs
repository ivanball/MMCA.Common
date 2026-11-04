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

    // ── CacheSettings.LocalCacheDuration validation ──
    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:05")]
    [InlineData("00:00:00.500")]
    public void CacheSettings_WithALocalCacheDurationUnderOneSecond_FailsValidation(string duration)
    {
        var settings = new CacheSettings { LocalCacheDuration = TimeSpan.Parse(duration, System.Globalization.CultureInfo.InvariantCulture) };

        Validate(settings).Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(CacheSettings.LocalCacheDuration));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00:00:01")]
    [InlineData("00:05:00")]
    public void CacheSettings_WithAnUnsetOrPositiveLocalCacheDuration_PassesValidation(string? duration)
    {
        var settings = new CacheSettings { LocalCacheDuration = duration is null ? null : TimeSpan.Parse(duration, System.Globalization.CultureInfo.InvariantCulture) };

        Validate(settings).Should().BeEmpty();
    }

    private static List<System.ComponentModel.DataAnnotations.ValidationResult> Validate(CacheSettings settings)
    {
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(settings, new System.ComponentModel.DataAnnotations.ValidationContext(settings), results, validateAllProperties: true);
        return results;
    }
}
