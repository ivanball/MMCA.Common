using AwesomeAssertions;
using MMCA.Common.API.Concurrency;

namespace MMCA.Common.API.Tests.Concurrency;

/// <summary>
/// Round-trip and tolerance rules for the weak entity tag that carries the framework's
/// optimistic-concurrency token.
/// </summary>
public sealed class ConcurrencyETagTests
{
    private static readonly byte[] RowVersion = [0, 0, 0, 0, 0, 0, 7, 209];

    [Fact]
    public void Format_ProducesAWeakBase64Tag() =>
        ConcurrencyETag.Format(RowVersion).Should().Be("W/\"AAAAAAAAB9E=\"");

    [Fact]
    public void FormatThenTryParse_RoundTripsTheToken()
    {
        ConcurrencyETag.TryParse(ConcurrencyETag.Format(RowVersion), out var parsed).Should().BeTrue();
        parsed.Should().Equal(RowVersion);
    }

    [Theory]
    [InlineData("W/\"AAAAAAAAB9E=\"")]
    [InlineData("w/\"AAAAAAAAB9E=\"")]
    [InlineData("\"AAAAAAAAB9E=\"")]
    [InlineData("  W/\"AAAAAAAAB9E=\"  ")]
    [InlineData("W/\"AAAAAAAAB9E=\", W/\"AAAAAAAAB9I=\"")]
    public void TryParse_AcceptsTheShapesAClientMaySend(string header)
    {
        ConcurrencyETag.TryParse(header, out var parsed).Should().BeTrue();
        parsed.Should().Equal(RowVersion, "only the first tag of a list is a meaningful precondition here");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("W/\"\"")]
    [InlineData("not base64 at all!")]
    [InlineData("\"@@@@\"")]
    public void TryParse_RejectsWhatIsNotAConcreteToken(string? header)
    {
        ConcurrencyETag.TryParse(header, out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
    }
}
