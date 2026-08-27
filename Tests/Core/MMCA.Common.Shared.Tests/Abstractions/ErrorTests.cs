using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Tests.Abstractions;

public class ErrorTests
{
    // ── Factory methods ──
    [Fact]
    public void Validation_CreatesErrorWithValidationType() =>
        Error.Validation("code", "msg").Type.Should().Be(ErrorType.Validation);

    [Fact]
    public void Invariant_CreatesErrorWithInvariantType() =>
        Error.Invariant("code", "msg").Type.Should().Be(ErrorType.Invariant);

    [Fact]
    public void NotFoundError_CreatesErrorWithNotFoundType() =>
        Error.NotFoundError("code", "msg").Type.Should().Be(ErrorType.NotFound);

    [Fact]
    public void Conflict_CreatesErrorWithConflictType() =>
        Error.Conflict("code", "msg").Type.Should().Be(ErrorType.Conflict);

    [Fact]
    public void Unauthorized_CreatesErrorWithUnauthorizedType() =>
        Error.Unauthorized("code", "msg").Type.Should().Be(ErrorType.Unauthorized);

    [Fact]
    public void Forbidden_CreatesErrorWithForbiddenType() =>
        Error.Forbidden("code", "msg").Type.Should().Be(ErrorType.Forbidden);

    [Fact]
    public void Failure_CreatesErrorWithFailureType() =>
        Error.Failure("code", "msg").Type.Should().Be(ErrorType.Failure);

    [Fact]
    public void Unexpected_CreatesErrorWithUnexpectedType() =>
        Error.Unexpected("code", "msg").Type.Should().Be(ErrorType.Unexpected);

    [Fact]
    public void Unexpected_WithSourceAndTarget_SetsProperties()
    {
        var error = Error.Unexpected("Payment.GatewayDown", "The gateway did not answer", "ChargeCard", "Gateway");

        error.Code.Should().Be("Payment.GatewayDown");
        error.Message.Should().Be("The gateway did not answer");
        error.Type.Should().Be(ErrorType.Unexpected);
        error.Source.Should().Be("ChargeCard");
        error.Target.Should().Be("Gateway");
    }

    // ── Source and Target ──
    [Fact]
    public void Validation_WithSourceAndTarget_SetsProperties()
    {
        var error = Error.Validation("code", "msg", "src", "tgt");

        error.Source.Should().Be("src");
        error.Target.Should().Be("tgt");
    }

    [Fact]
    public void WithSource_ReturnsNewErrorWithSource()
    {
        var original = Error.Validation("code", "msg");
        var withSource = original.WithSource("MySrc");

        withSource.Source.Should().Be("MySrc");
        original.Source.Should().BeNull();
    }

    [Fact]
    public void WithTarget_ReturnsNewErrorWithTarget()
    {
        var original = Error.Validation("code", "msg");
        var withTarget = original.WithTarget("MyTgt");

        withTarget.Target.Should().Be("MyTgt");
        original.Target.Should().BeNull();
    }

    // ── Static instances ──
    [Fact]
    public void NotFound_StaticInstance_HasCorrectType() =>
        Error.NotFound.Type.Should().Be(ErrorType.NotFound);

    [Fact]
    public void AlreadyDeleted_StaticInstance_HasConflictType() =>
        Error.AlreadyDeleted.Type.Should().Be(ErrorType.Conflict);

    [Fact]
    public void InvalidEntityField_StaticInstance_HasValidationType() =>
        Error.InvalidEntityField.Type.Should().Be(ErrorType.Validation);
}
