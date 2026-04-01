using AwesomeAssertions;
using MMCA.Common.API.Idempotency;

namespace MMCA.Common.API.Tests.Idempotency;

public class IdempotencySettingsTests
{
    [Fact]
    public void SectionName_IsIdempotency() =>
        IdempotencySettings.SectionName.Should().Be("Idempotency");

    [Fact]
    public void Default_CacheExpirationHours_Is24() =>
        new IdempotencySettings().CacheExpirationHours.Should().Be(24);

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new IdempotencySettings { CacheExpirationHours = 48 };
        sut.CacheExpirationHours.Should().Be(48);
    }
}
