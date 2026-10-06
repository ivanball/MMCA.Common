using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.Repositories;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// L122: a keyset cursor's date and time sort values must round-trip with their kind and offset,
/// or the seek boundary moves by the host's UTC offset.
/// </summary>
public sealed class KeysetQueryBuilderCursorValueTests
{
    [Fact]
    public void TryFromInvariantString_UtcDateTime_RoundTripsAsUtc()
    {
        var utc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        KeysetQueryBuilder.TryFromInvariantString(typeof(DateTime), KeysetQueryBuilder.ToInvariantString(utc)!, out var value)
            .Should().BeTrue();

        value.Should().BeOfType<DateTime>().Which.Kind.Should().Be(DateTimeKind.Utc);
        ((DateTime)value!).Should().Be(utc);
    }

    [Fact]
    public void TryFromInvariantString_DateTimeOffset_KeepsItsOffset()
    {
        var stamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(-5));

        KeysetQueryBuilder.TryFromInvariantString(typeof(DateTimeOffset?), KeysetQueryBuilder.ToInvariantString(stamp)!, out var value)
            .Should().BeTrue();

        value.Should().BeOfType<DateTimeOffset>().Which.Offset.Should().Be(TimeSpan.FromHours(-5));
        ((DateTimeOffset)value!).Should().Be(stamp);
    }

    [Fact]
    public void TryFromInvariantString_MalformedDateTime_ReturnsFalse() =>
        KeysetQueryBuilder.TryFromInvariantString(typeof(DateTime), "not-a-date", out _).Should().BeFalse();
}
