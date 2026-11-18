using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class ValidatingCommandDecoratorTests : ValidatingDecoratorTestsBase<TestValidatingCommand>
{
    // ── Command only: a passing validator does not mask a failing one ──
    [Fact]
    public async Task HandleAsync_TwoValidators_FirstPasses_StillHonorsTheSecond()
    {
        var inner = new Mock<ICommandHandler<TestValidatingCommand, Result>>();

        var passing = new Mock<IValidator<TestValidatingCommand>>();
        passing.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var failing = new Mock<IValidator<TestValidatingCommand>>();
        failing.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("Name", "Name must be unique")]));

        IEnumerable<IValidator<TestValidatingCommand>> validators = [passing.Object, failing.Object];
        var sut = new ValidatingCommandDecorator<TestValidatingCommand, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingCommandDecorator<TestValidatingCommand, Result>>.Instance);

        Result result = await sut.HandleAsync(new TestValidatingCommand("valid"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Message == "Name must be unique");
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Command only: a non-Result handler with a validator registered ──
    [Fact]
    public async Task HandleAsync_NonResultTResult_ValidationPasses_CallsInnerHandler()
    {
        var inner = new Mock<ICommandHandler<TestValidatingCommand, string>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("handled");

        var validator = new Mock<IValidator<TestValidatingCommand>>();
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        IEnumerable<IValidator<TestValidatingCommand>> validators = [validator.Object];
        var sut = new ValidatingCommandDecorator<TestValidatingCommand, string>(
            inner.Object,
            validators,
            NullLogger<ValidatingCommandDecorator<TestValidatingCommand, string>>.Instance);

        var result = await sut.HandleAsync(new TestValidatingCommand("valid"));

        result.Should().Be("handled");
    }

    [Fact]
    public async Task HandleAsync_NonResultTResult_ValidationFails_FailsOnlyOnTheShortCircuit()
    {
        var inner = new Mock<ICommandHandler<TestValidatingCommand, string>>();

        var validator = new Mock<IValidator<TestValidatingCommand>>();
        var failures = new List<ValidationFailure>
        {
            new("Name", "Name is required")
        };
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(failures));

        IEnumerable<IValidator<TestValidatingCommand>> validators = [validator.Object];
        var sut = new ValidatingCommandDecorator<TestValidatingCommand, string>(
            inner.Object,
            validators,
            NullLogger<ValidatingCommandDecorator<TestValidatingCommand, string>>.Instance);

        // Fabricating a failure is genuinely impossible for this TResult, so the short-circuit path
        // still fails: as the factory's own InvalidOperationException, at the point of use.
        Func<Task> act = () => sut.HandleAsync(new TestValidatingCommand(string.Empty));

        await act.Should().ThrowAsync<InvalidOperationException>();
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    protected override TestValidatingCommand CreateRequest(string value) => new(value);

    protected override Func<TestValidatingCommand, Task<T>> CreateSut<T>(
        IEnumerable<IValidator<TestValidatingCommand>> validators,
        Func<Task<T>> inner)
    {
        var handler = new Mock<ICommandHandler<TestValidatingCommand, T>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() => inner());

        var sut = new ValidatingCommandDecorator<TestValidatingCommand, T>(
            handler.Object,
            validators,
            NullLogger<ValidatingCommandDecorator<TestValidatingCommand, T>>.Instance);

        return command => sut.HandleAsync(command);
    }
}

// ── Test helpers ──
public sealed record TestValidatingCommand(string Name);
