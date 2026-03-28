using AwesomeAssertions;
using MMCA.Common.Shared.Exceptions;

namespace MMCA.Common.Shared.Tests.Exceptions;

public class DomainExceptionTests
{
    private sealed class ConcreteDomainException : DomainException
    {
        public ConcreteDomainException() { }

        public ConcreteDomainException(string message)
            : base(message) { }

        public ConcreteDomainException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    // ── DomainException ──
    [Fact]
    public void DefaultConstructor_CreatesException() =>
        new ConcreteDomainException().Message.Should().NotBeNullOrEmpty();

    [Fact]
    public void MessageConstructor_SetsMessage() =>
        new ConcreteDomainException("test error").Message.Should().Be("test error");

    [Fact]
    public void InnerExceptionConstructor_SetsInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new ConcreteDomainException("outer", inner);
        ex.Message.Should().Be("outer");
        ex.InnerException.Should().Be(inner);
    }

    // ── DomainInvariantViolationException ──
    [Fact]
    public void InvariantViolation_DefaultConstructor_CreatesException() =>
        new DomainInvariantViolationException().Should().BeAssignableTo<DomainException>();

    [Fact]
    public void InvariantViolation_MessageConstructor_SetsMessage() =>
        new DomainInvariantViolationException("invariant broken").Message
            .Should().Be("invariant broken");

    [Fact]
    public void InvariantViolation_InnerExceptionConstructor_SetsInnerException()
    {
        var inner = new ArgumentException("arg");
        var ex = new DomainInvariantViolationException("outer", inner);
        ex.InnerException.Should().Be(inner);
    }

    [Fact]
    public void InvariantViolation_IsDomainException() =>
        new DomainInvariantViolationException().Should().BeAssignableTo<DomainException>();
}
