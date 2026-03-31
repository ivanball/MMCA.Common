using AwesomeAssertions;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class CorrelationContextTests
{
    // ── Default correlation ID ──
    [Fact]
    public void CorrelationId_WhenNotSet_IsNonEmptyGuid()
    {
        var sut = new CorrelationContext();

        sut.CorrelationId.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(sut.CorrelationId, out _).Should().BeTrue();
    }

    // ── Set correlation ID ──
    [Fact]
    public void SetCorrelationId_WithValidValue_UpdatesCorrelationId()
    {
        var sut = new CorrelationContext();
        const string expectedId = "custom-correlation-id";

        sut.SetCorrelationId(expectedId);

        sut.CorrelationId.Should().Be(expectedId);
    }

    // ── Null or whitespace throws ──
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetCorrelationId_WhenNullOrWhitespace_ThrowsArgumentException(string? correlationId)
    {
        var sut = new CorrelationContext();

        FluentActions.Invoking(() => sut.SetCorrelationId(correlationId!))
            .Should().Throw<ArgumentException>();
    }

    // ── Two instances have different IDs ──
    [Fact]
    public void TwoInstances_HaveDifferentDefaultCorrelationIds()
    {
        var context1 = new CorrelationContext();
        var context2 = new CorrelationContext();

        context1.CorrelationId.Should().NotBe(context2.CorrelationId);
    }
}
