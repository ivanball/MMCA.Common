using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class ValidatingQueryDecoratorTests
{
    // ── No validators registered ──
    [Fact]
    public async Task HandleAsync_NoValidators_PassesThroughToInnerHandler()
    {
        var inner = new Mock<IQueryHandler<TestValidatingQuery, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        IEnumerable<IValidator<TestValidatingQuery>> validators = [];
        var sut = new ValidatingQueryDecorator<TestValidatingQuery, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingQueryDecorator<TestValidatingQuery, Result>>.Instance);

        Result result = await sut.HandleAsync(new TestValidatingQuery("valid"));

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Validation passes ──
    [Fact]
    public async Task HandleAsync_ValidationPasses_CallsInnerHandler()
    {
        var inner = new Mock<IQueryHandler<TestValidatingQuery, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var validator = new Mock<IValidator<TestValidatingQuery>>();
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        IEnumerable<IValidator<TestValidatingQuery>> validators = [validator.Object];
        var sut = new ValidatingQueryDecorator<TestValidatingQuery, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingQueryDecorator<TestValidatingQuery, Result>>.Instance);

        Result result = await sut.HandleAsync(new TestValidatingQuery("valid"));

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Validation fails ──
    [Fact]
    public async Task HandleAsync_ValidationFails_ReturnsFailureWithoutCallingInnerHandler()
    {
        var inner = new Mock<IQueryHandler<TestValidatingQuery, Result>>();

        var validator = new Mock<IValidator<TestValidatingQuery>>();
        var failures = new List<ValidationFailure>
        {
            new("PageSize", "PageSize must be greater than zero")
        };
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(failures));

        IEnumerable<IValidator<TestValidatingQuery>> validators = [validator.Object];
        var sut = new ValidatingQueryDecorator<TestValidatingQuery, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingQueryDecorator<TestValidatingQuery, Result>>.Instance);

        Result result = await sut.HandleAsync(new TestValidatingQuery(string.Empty));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().NotBeEmpty();
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Aggregation: every failure reaches the Result ──
    [Fact]
    public async Task HandleAsync_ValidationFails_AggregatesAllFailures()
    {
        var inner = new Mock<IQueryHandler<TestValidatingQuery, Result>>();

        var validator = new Mock<IValidator<TestValidatingQuery>>();
        var failures = new List<ValidationFailure>
        {
            new("PageSize", "PageSize must be greater than zero"),
            new("SortBy", "SortBy is not a known column"),
        };
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(failures));

        IEnumerable<IValidator<TestValidatingQuery>> validators = [validator.Object];
        var sut = new ValidatingQueryDecorator<TestValidatingQuery, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingQueryDecorator<TestValidatingQuery, Result>>.Instance);

        Result result = await sut.HandleAsync(new TestValidatingQuery(string.Empty));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().HaveCount(2);
        result.Errors.Should().OnlyContain(e => e.Type == ErrorType.Validation);
    }

    // ── Result<T> generic variant ──
    [Fact]
    public async Task HandleAsync_GenericResult_ValidationFails_ReturnsTypedFailure()
    {
        var inner = new Mock<IQueryHandler<TestValidatingQuery, Result<int>>>();

        var validator = new Mock<IValidator<TestValidatingQuery>>();
        var failures = new List<ValidationFailure>
        {
            new("PageSize", "PageSize must be greater than zero")
        };
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult(failures));

        IEnumerable<IValidator<TestValidatingQuery>> validators = [validator.Object];
        var sut = new ValidatingQueryDecorator<TestValidatingQuery, Result<int>>(
            inner.Object,
            validators,
            NullLogger<ValidatingQueryDecorator<TestValidatingQuery, Result<int>>>.Instance);

        Result<int> result = await sut.HandleAsync(new TestValidatingQuery(string.Empty));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().NotBeEmpty();
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_GenericResult_ValidationPasses_ReturnsValue()
    {
        var inner = new Mock<IQueryHandler<TestValidatingQuery, Result<int>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(42));

        var validator = new Mock<IValidator<TestValidatingQuery>>();
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        IEnumerable<IValidator<TestValidatingQuery>> validators = [validator.Object];
        var sut = new ValidatingQueryDecorator<TestValidatingQuery, Result<int>>(
            inner.Object,
            validators,
            NullLogger<ValidatingQueryDecorator<TestValidatingQuery, Result<int>>>.Instance);

        Result<int> result = await sut.HandleAsync(new TestValidatingQuery("valid"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    // ── First registered validator wins, exactly like the command twin ──
    [Fact]
    public async Task HandleAsync_MultipleValidators_UsesOnlyTheFirst()
    {
        var inner = new Mock<IQueryHandler<TestValidatingQuery, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var first = new Mock<IValidator<TestValidatingQuery>>();
        first.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var second = new Mock<IValidator<TestValidatingQuery>>();
        second.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult([new ValidationFailure("PageSize", "never reached")]));

        IEnumerable<IValidator<TestValidatingQuery>> validators = [first.Object, second.Object];
        var sut = new ValidatingQueryDecorator<TestValidatingQuery, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingQueryDecorator<TestValidatingQuery, Result>>.Instance);

        Result result = await sut.HandleAsync(new TestValidatingQuery("valid"));

        result.IsSuccess.Should().BeTrue();
        second.Verify(
            x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── A handler whose TResult is neither Result nor Result<T>: the failure factory must stay lazy,
    //    so an always-valid query still resolves and runs (mirrors the command decorator's test). ──
    [Fact]
    public async Task HandleAsync_NonResultTResult_NoValidators_PassesThroughToInnerHandler()
    {
        var inner = new Mock<IQueryHandler<TestValidatingQuery, string>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("handled");

        IEnumerable<IValidator<TestValidatingQuery>> validators = [];
        var sut = new ValidatingQueryDecorator<TestValidatingQuery, string>(
            inner.Object,
            validators,
            NullLogger<ValidatingQueryDecorator<TestValidatingQuery, string>>.Instance);

        var result = await sut.HandleAsync(new TestValidatingQuery("valid"));

        result.Should().Be("handled");
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_PassesCancellationTokenToValidatorAndInnerHandler()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;

        var inner = new Mock<IQueryHandler<TestValidatingQuery, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), token))
            .ReturnsAsync(Result.Success());

        var validator = new Mock<IValidator<TestValidatingQuery>>();
        validator.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), token))
            .ReturnsAsync(new ValidationResult());

        IEnumerable<IValidator<TestValidatingQuery>> validators = [validator.Object];
        var sut = new ValidatingQueryDecorator<TestValidatingQuery, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingQueryDecorator<TestValidatingQuery, Result>>.Instance);

        await sut.HandleAsync(new TestValidatingQuery("valid"), token);

        validator.Verify(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), token), Times.Once);
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), token), Times.Once);
    }
}

// ── Test helpers ──
public sealed record TestValidatingQuery(string SortBy);
