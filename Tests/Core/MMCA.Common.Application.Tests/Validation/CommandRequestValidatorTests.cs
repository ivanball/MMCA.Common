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

    // ── Uses only the first registered validator ──
    [Fact]
    public void Validate_MultipleRequestValidatorsRegistered_UsesFirstValidator()
    {
        IEnumerable<IValidator<TestRequest>> requestValidators =
        [
            new TestRequestValidator(),
            new PermissiveTestRequestValidator()
        ];
        var sut = new CommandRequestValidator<TestCommandWithRequest, TestRequest>(requestValidators);

        TestValidationResult<TestCommandWithRequest> result = sut.TestValidate(
            new TestCommandWithRequest(new TestRequest(string.Empty)));

        result.ShouldHaveValidationErrorFor(c => c.Request.Name);
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

public sealed class PermissiveTestRequestValidator : AbstractValidator<TestRequest>
{
    // No rules — always passes
}
