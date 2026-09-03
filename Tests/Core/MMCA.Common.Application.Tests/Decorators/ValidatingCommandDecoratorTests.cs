using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class ValidatingCommandDecoratorTests
{
    // ── No validators registered ──
    [Fact]
    public async Task HandleAsync_NoValidators_PassesThroughToInnerHandler()
    {
        var inner = new Mock<ICommandHandler<TestValidatingCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        IEnumerable<IValidator<TestValidatingCommand>> validators = [];
        var sut = new ValidatingCommandDecorator<TestValidatingCommand, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingCommandDecorator<TestValidatingCommand, Result>>.Instance);

        Result result = await sut.HandleAsync(new TestValidatingCommand("valid"));

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Validation passes ──
    [Fact]
    public async Task HandleAsync_ValidationPasses_CallsInnerHandler()
    {
        var inner = new Mock<ICommandHandler<TestValidatingCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var validator = new Mock<IValidator<TestValidatingCommand>>();
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        IEnumerable<IValidator<TestValidatingCommand>> validators = [validator.Object];
        var sut = new ValidatingCommandDecorator<TestValidatingCommand, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingCommandDecorator<TestValidatingCommand, Result>>.Instance);

        Result result = await sut.HandleAsync(new TestValidatingCommand("valid"));

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Validation fails ──
    [Fact]
    public async Task HandleAsync_ValidationFails_ReturnsFailureWithoutCallingInnerHandler()
    {
        var inner = new Mock<ICommandHandler<TestValidatingCommand, Result>>();

        var validator = new Mock<IValidator<TestValidatingCommand>>();
        var failures = new List<ValidationFailure>
        {
            new("Name", "Name is required")
        };
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(failures));

        IEnumerable<IValidator<TestValidatingCommand>> validators = [validator.Object];
        var sut = new ValidatingCommandDecorator<TestValidatingCommand, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingCommandDecorator<TestValidatingCommand, Result>>.Instance);

        Result result = await sut.HandleAsync(new TestValidatingCommand(string.Empty));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().NotBeEmpty();
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Every registered validator runs ──
    [Fact]
    public async Task HandleAsync_TwoValidators_RunsBothAndUnionsTheirFailures()
    {
        // A command commonly carries a module-authored validator beside a cross-cutting one. Honoring
        // only the first registration would leave the second one's rules silently unenforced.
        var inner = new Mock<ICommandHandler<TestValidatingCommand, Result>>();

        var first = new Mock<IValidator<TestValidatingCommand>>();
        first.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("Name", "Name is required")]));

        var second = new Mock<IValidator<TestValidatingCommand>>();
        second.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("Name", "Name must be unique")]));

        IEnumerable<IValidator<TestValidatingCommand>> validators = [first.Object, second.Object];
        var sut = new ValidatingCommandDecorator<TestValidatingCommand, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingCommandDecorator<TestValidatingCommand, Result>>.Instance);

        Result result = await sut.HandleAsync(new TestValidatingCommand(string.Empty));

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Message).Should().BeEquivalentTo(
            ["Name is required", "Name must be unique"],
            "the caller sees every broken rule in one response, not one per round trip");
        first.Verify(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        second.Verify(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

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

    // ── Result<T> generic variant ──
    [Fact]
    public async Task HandleAsync_GenericResult_ValidationFails_ReturnsTypedFailure()
    {
        var inner = new Mock<ICommandHandler<TestValidatingCommand, Result<int>>>();

        var validator = new Mock<IValidator<TestValidatingCommand>>();
        var failures = new List<ValidationFailure>
        {
            new("Name", "Name is required")
        };
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(failures));

        IEnumerable<IValidator<TestValidatingCommand>> validators = [validator.Object];
        var sut = new ValidatingCommandDecorator<TestValidatingCommand, Result<int>>(
            inner.Object,
            validators,
            NullLogger<ValidatingCommandDecorator<TestValidatingCommand, Result<int>>>.Instance);

        Result<int> result = await sut.HandleAsync(new TestValidatingCommand(string.Empty));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().NotBeEmpty();
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── A handler whose TResult is neither Result nor Result<T> ──
    // Scrutor's TryDecorate is unconditional, so such a handler gets decorated too. Building the
    // failure delegate eagerly (in the static constructor) turned that into a
    // TypeInitializationException the moment the decorator was RESOLVED, even for a command that
    // always validates cleanly.
    [Fact]
    public async Task HandleAsync_NonResultTResult_NoValidators_PassesThroughToInnerHandler()
    {
        var inner = new Mock<ICommandHandler<TestValidatingCommand, string>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("handled");

        IEnumerable<IValidator<TestValidatingCommand>> validators = [];
        var sut = new ValidatingCommandDecorator<TestValidatingCommand, string>(
            inner.Object,
            validators,
            NullLogger<ValidatingCommandDecorator<TestValidatingCommand, string>>.Instance);

        var result = await sut.HandleAsync(new TestValidatingCommand("valid"));

        result.Should().Be("handled");
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

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

    // ── Result<T> generic variant passes through ──
    [Fact]
    public async Task HandleAsync_GenericResult_ValidationPasses_CallsInnerHandler()
    {
        var inner = new Mock<ICommandHandler<TestValidatingCommand, Result<int>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(42));

        var validator = new Mock<IValidator<TestValidatingCommand>>();
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        IEnumerable<IValidator<TestValidatingCommand>> validators = [validator.Object];
        var sut = new ValidatingCommandDecorator<TestValidatingCommand, Result<int>>(
            inner.Object,
            validators,
            NullLogger<ValidatingCommandDecorator<TestValidatingCommand, Result<int>>>.Instance);

        Result<int> result = await sut.HandleAsync(new TestValidatingCommand("valid"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }
}

// ── Test helpers ──
public sealed record TestValidatingCommand(string Name);
