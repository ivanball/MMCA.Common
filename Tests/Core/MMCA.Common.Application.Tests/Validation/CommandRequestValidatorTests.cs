using AwesomeAssertions;
using FluentValidation;
using FluentValidation.TestHelper;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.Validation;

namespace MMCA.Common.Application.Tests.Validation;

public sealed class CommandRequestValidatorTests
{
    // ── No request validator registered ──
    [Fact]
    public void Validate_NoRequestValidatorRegistered_PassesValidation()
    {
        IEnumerable<IValidator<TestRequest>> requestValidators = [];
        var sut = new CommandRequestValidator<TestCommandWithRequest, TestRequest>(requestValidators);

        TestValidationResult<TestCommandWithRequest> result = sut.TestValidate(
            new TestCommandWithRequest(new TestRequest(string.Empty)));

        result.ShouldNotHaveAnyValidationErrors();
    }

    // ── Request validator passes ──
    [Fact]
    public void Validate_RequestIsValid_NoValidationErrors()
    {
        IEnumerable<IValidator<TestRequest>> requestValidators = [new TestRequestValidator()];
        var sut = new CommandRequestValidator<TestCommandWithRequest, TestRequest>(requestValidators);

        TestValidationResult<TestCommandWithRequest> result = sut.TestValidate(
            new TestCommandWithRequest(new TestRequest("Valid Name")));

        result.ShouldNotHaveAnyValidationErrors();
    }

    // ── Request validator fails ──
    [Fact]
    public void Validate_RequestIsInvalid_HasValidationErrors()
    {
        IEnumerable<IValidator<TestRequest>> requestValidators = [new TestRequestValidator()];
        var sut = new CommandRequestValidator<TestCommandWithRequest, TestRequest>(requestValidators);

        TestValidationResult<TestCommandWithRequest> result = sut.TestValidate(
            new TestCommandWithRequest(new TestRequest(string.Empty)));

        result.ShouldHaveValidationErrorFor(c => c.Request.Name)
            .WithErrorMessage("Name is required");
    }

    // ── Every registered validator runs, not just the first ──
    [Fact]
    public void Validate_MultipleRequestValidatorsRegistered_RunsEveryValidator()
    {
        IEnumerable<IValidator<TestRequest>> requestValidators =
        [
            new TestRequestValidator(),
            new SecondTestRequestValidator()
        ];
        var sut = new CommandRequestValidator<TestCommandWithRequest, TestRequest>(requestValidators);

        FluentValidation.Results.ValidationResult result = sut.Validate(
            new TestCommandWithRequest(new TestRequest(string.Empty)));

        result.Errors.Select(e => e.ErrorMessage).Should()
            .Contain("Name is required").And
            .Contain(
                "Name must be on the approved list",
                "honoring only the first registration turns every other validator's rules into dead code");
    }

    // ── The same validator class registered twice must not double-report ──
    [Fact]
    public void Validate_TheSameValidatorTypeRegisteredTwice_ReportsEachFailureOnce()
    {
        IEnumerable<IValidator<TestRequest>> requestValidators =
        [
            new TestRequestValidator(),
            new TestRequestValidator()
        ];
        var sut = new CommandRequestValidator<TestCommandWithRequest, TestRequest>(requestValidators);

        FluentValidation.Results.ValidationResult result = sut.Validate(
            new TestCommandWithRequest(new TestRequest(string.Empty)));

        result.Errors.Should().ContainSingle(
            "duplicate registrations of one validator class are de-duplicated by runtime type");
    }

    // ── A permissive validator alongside a strict one still reports the strict failure ──
    [Fact]
    public void Validate_APermissiveValidatorRegisteredFirst_StillReportsTheStrictFailure()
    {
        IEnumerable<IValidator<TestRequest>> requestValidators =
        [
            new PermissiveTestRequestValidator(),
            new TestRequestValidator()
        ];
        var sut = new CommandRequestValidator<TestCommandWithRequest, TestRequest>(requestValidators);

        TestValidationResult<TestCommandWithRequest> result = sut.TestValidate(
            new TestCommandWithRequest(new TestRequest(string.Empty)));

        result.ShouldHaveValidationErrorFor(c => c.Request.Name)
            .WithErrorMessage("Name is required");
    }
}

// ── Test helpers ──
public sealed record TestRequest(string Name);

public sealed record TestCommandWithRequest(TestRequest Request) : ICommandWithRequest<TestRequest>;

public sealed class TestRequestValidator : AbstractValidator<TestRequest>
{
    public TestRequestValidator() =>
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("Name is required");
}

public sealed class SecondTestRequestValidator : AbstractValidator<TestRequest>
{
    public SecondTestRequestValidator() =>
        RuleFor(x => x.Name)
            .Must(name => string.Equals(name, "Approved", StringComparison.Ordinal))
            .WithMessage("Name must be on the approved list");
}

public sealed class PermissiveTestRequestValidator : AbstractValidator<TestRequest>
{
    // No rules: always passes.
}
