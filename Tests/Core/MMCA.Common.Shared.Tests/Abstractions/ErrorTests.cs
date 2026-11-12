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
    public void TooManyRequests_CreatesErrorWithTooManyRequestsType() =>
        Error.TooManyRequests("code", "msg").Type.Should().Be(ErrorType.TooManyRequests);

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

    // ── For<TTarget>: source plus the target type's simple name in one call ──
    [Fact]
    public void For_SetsSourceAndTargetFromTheTypeName()
    {
        var error = Error.Validation("code", "msg").For<ErrorForTestOrder>("PlaceOrder");

        error.Source.Should().Be("PlaceOrder");
        error.Target.Should().Be(
            "ErrorForTestOrder",
            "the target is the type's simple Name, not its FullName");
    }

    [Fact]
    public void For_KeepsCodeMessageAndType()
    {
        var error = Error.Conflict("Order.Duplicate", "The order already exists").For<ErrorForTestOrder>("PlaceOrder");

        error.Code.Should().Be("Order.Duplicate");
        error.Message.Should().Be("The order already exists");
        error.Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public void For_ReplacesAnExistingSourceAndTarget()
    {
        var error = Error.Validation("code", "msg", "OldSource", "OldTarget").For<ErrorForTestLine>("AddLine");

        error.Source.Should().Be("AddLine");
        error.Target.Should().Be("ErrorForTestLine");
    }

    [Fact]
    public void For_LeavesTheOriginalErrorUnchanged()
    {
        var original = Error.Validation("code", "msg");

        _ = original.For<ErrorForTestOrder>("PlaceOrder");

        original.Source.Should().BeNull();
        original.Target.Should().BeNull();
    }

    [Fact]
    public void For_OnTheSharedNotFoundInstance_KeepsNotFoundAndDoesNotMutateTheStatic()
    {
        var error = Error.NotFound.For<ErrorForTestOrder>("GetOrder");

        error.Type.Should().Be(ErrorType.NotFound);
        error.Code.Should().Be(Error.NotFound.Code);
        error.Source.Should().Be("GetOrder");
        error.Target.Should().Be("ErrorForTestOrder");
        Error.NotFound.Source.Should().BeNull("Error.NotFound is a shared static and must never pick up a caller's context");
        Error.NotFound.Target.Should().BeNull();
    }

    [Fact]
    public void For_EqualsWithSourceThenWithTarget()
    {
        var viaFor = Error.NotFound.For<ErrorForTestLine>("RemoveLine");
        var viaChain = Error.NotFound.WithSource("RemoveLine").WithTarget(nameof(ErrorForTestLine));

        viaFor.Should().Be(viaChain);
    }

    [Fact]
    public void For_DoesNotValidateItsArgument()
    {
        var act = () => Error.Validation("code", "msg").For<ErrorForTestOrder>(string.Empty);

        act.Should().NotThrow("For<TTarget> does no validation, exactly like WithSource and WithTarget");
        act().Source.Should().BeEmpty();
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

    // ── Target types for For<TTarget> (only their names matter) ──
    private sealed class ErrorForTestOrder;

    private sealed class ErrorForTestLine;
}
