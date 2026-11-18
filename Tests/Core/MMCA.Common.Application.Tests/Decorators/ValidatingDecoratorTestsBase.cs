using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// The validating decorator contract shared by the command and the query pipeline: no validator or
/// a passing one reaches the handler, a failing one short-circuits without it, every registered
/// validator runs and their failures are unioned, Result&lt;T&gt; gets a typed failure, and a
/// non-Result handler still resolves. Each subclass supplies the decorator under test and its
/// request type.
/// </summary>
/// <typeparam name="TRequest">The request type the subclass validates.</typeparam>
public abstract class ValidatingDecoratorTestsBase<TRequest>
    where TRequest : class
{
    /// <summary>Builds a request whose single string member is <paramref name="value"/>.</summary>
    protected abstract TRequest CreateRequest(string value);

    /// <summary>
    /// Builds the decorator over <paramref name="validators"/> and an inner handler that answers
    /// through <paramref name="inner"/>, and returns a call that sends it one request.
    /// </summary>
    /// <typeparam name="T">The handler's result type (a non-Result type exercises the lazy factory).</typeparam>
    protected abstract Func<TRequest, Task<T>> CreateSut<T>(
        IEnumerable<IValidator<TRequest>> validators,
        Func<Task<T>> inner);

    // ── No validators registered ──
    [Fact]
    public async Task HandleAsync_NoValidators_PassesThroughToInnerHandler()
    {
        var calls = 0;
        IEnumerable<IValidator<TRequest>> validators = [];
        var handle = CreateSut(
            validators,
            () =>
            {
                calls++;
                return Task.FromResult(Result.Success());
            });

        Result result = await handle(CreateRequest("valid"));

        result.IsSuccess.Should().BeTrue();
        calls.Should().Be(1);
    }

    // ── Validation passes ──
    [Fact]
    public async Task HandleAsync_ValidationPasses_CallsInnerHandler()
    {
        var calls = 0;
        var validator = new Mock<IValidator<TRequest>>();
        validator.Setup(x => x.ValidateAsync(It.IsAny<TRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        IEnumerable<IValidator<TRequest>> validators = [validator.Object];
        var handle = CreateSut(
            validators,
            () =>
            {
                calls++;
                return Task.FromResult(Result.Success());
            });

        Result result = await handle(CreateRequest("valid"));

        result.IsSuccess.Should().BeTrue();
        calls.Should().Be(1);
    }

    // ── Validation fails ──
    [Fact]
    public async Task HandleAsync_ValidationFails_ReturnsFailureWithoutCallingInnerHandler()
    {
        var calls = 0;
        var validator = new Mock<IValidator<TRequest>>();
        var failures = new List<ValidationFailure>
        {
            new("Name", "Name is required")
        };
        validator.Setup(x => x.ValidateAsync(It.IsAny<TRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(failures));

        IEnumerable<IValidator<TRequest>> validators = [validator.Object];
        var handle = CreateSut(
            validators,
            () =>
            {
                calls++;
                return Task.FromResult(Result.Success());
            });

        Result result = await handle(CreateRequest(string.Empty));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().NotBeEmpty();
        calls.Should().Be(0);
    }

    // ── Every registered validator runs ──
    [Fact]
    public async Task HandleAsync_MultipleValidators_RunsThemAllAndUnionsTheirFailures()
    {
        // A request commonly carries a module-authored validator beside a cross-cutting one. Honoring
        // only the first registration would leave the second one's rules silently unenforced.
        var calls = 0;

        var first = new Mock<IValidator<TRequest>>();
        first.Setup(x => x.ValidateAsync(It.IsAny<TRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("Name", "Name is required")]));

        var second = new Mock<IValidator<TRequest>>();
        second.Setup(x => x.ValidateAsync(It.IsAny<TRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("Name", "Name must be unique")]));

        IEnumerable<IValidator<TRequest>> validators = [first.Object, second.Object];
        var handle = CreateSut(
            validators,
            () =>
            {
                calls++;
                return Task.FromResult(Result.Success());
            });

        Result result = await handle(CreateRequest(string.Empty));

        result.IsFailure.Should().BeTrue();
        result.Errors.Select(e => e.Message).Should().BeEquivalentTo(
            ["Name is required", "Name must be unique"],
            "the caller sees every broken rule in one response, not one per round trip");
        first.Verify(x => x.ValidateAsync(It.IsAny<TRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        second.Verify(x => x.ValidateAsync(It.IsAny<TRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        calls.Should().Be(0);
    }

    // ── Result<T> generic variant ──
    [Fact]
    public async Task HandleAsync_GenericResult_ValidationFails_ReturnsTypedFailure()
    {
        var calls = 0;
        var validator = new Mock<IValidator<TRequest>>();
        var failures = new List<ValidationFailure>
        {
            new("Name", "Name is required")
        };
        validator.Setup(x => x.ValidateAsync(It.IsAny<TRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(failures));

        IEnumerable<IValidator<TRequest>> validators = [validator.Object];
        var handle = CreateSut(
            validators,
            () =>
            {
                calls++;
                return Task.FromResult(Result.Success(1));
            });

        Result<int> result = await handle(CreateRequest(string.Empty));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().NotBeEmpty();
        calls.Should().Be(0);
    }

    // ── Result<T> generic variant passes through ──
    [Fact]
    public async Task HandleAsync_GenericResult_ValidationPasses_ReturnsValue()
    {
        var validator = new Mock<IValidator<TRequest>>();
        validator.Setup(x => x.ValidateAsync(It.IsAny<TRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        IEnumerable<IValidator<TRequest>> validators = [validator.Object];
        var handle = CreateSut(validators, () => Task.FromResult(Result.Success(42)));

        Result<int> result = await handle(CreateRequest("valid"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    // ── A handler whose TResult is neither Result nor Result<T> ──
    // Scrutor's TryDecorate is unconditional, so such a handler gets decorated too. Building the
    // failure delegate eagerly (in the static constructor) turned that into a
    // TypeInitializationException the moment the decorator was RESOLVED, even for a request that
    // always validates cleanly.
    [Fact]
    public async Task HandleAsync_NonResultTResult_NoValidators_PassesThroughToInnerHandler()
    {
        var calls = 0;
        IEnumerable<IValidator<TRequest>> validators = [];
        var handle = CreateSut(
            validators,
            () =>
            {
                calls++;
                return Task.FromResult("handled");
            });

        var result = await handle(CreateRequest("valid"));

        result.Should().Be("handled");
        calls.Should().Be(1);
    }
}
