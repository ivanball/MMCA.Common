using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class ValidatingQueryDecoratorTests : ValidatingDecoratorTestsBase<TestValidatingQuery>
{
    // ── Query only, aggregation: every failure reaches the Result ──
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

    // ── Query only: several validators that all pass reach the handler once ──
    [Fact]
    public async Task HandleAsync_MultipleValidators_AllPass_CallsInnerHandlerOnce()
    {
        var inner = new Mock<IQueryHandler<TestValidatingQuery, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var first = new Mock<IValidator<TestValidatingQuery>>();
        first.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        var second = new Mock<IValidator<TestValidatingQuery>>();
        second.Setup(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidationResult());

        IEnumerable<IValidator<TestValidatingQuery>> validators = [first.Object, second.Object];
        var sut = new ValidatingQueryDecorator<TestValidatingQuery, Result>(
            inner.Object,
            validators,
            NullLogger<ValidatingQueryDecorator<TestValidatingQuery, Result>>.Instance);

        Result result = await sut.HandleAsync(new TestValidatingQuery("valid"));

        result.IsSuccess.Should().BeTrue();
        second.Verify(x => x.ValidateAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        inner.Verify(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Query only: the caller's token reaches both the validator and the handler ──
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

    protected override TestValidatingQuery CreateRequest(string value) => new(value);

    protected override Func<TestValidatingQuery, Task<T>> CreateSut<T>(
        IEnumerable<IValidator<TestValidatingQuery>> validators,
        Func<Task<T>> inner)
    {
        var handler = new Mock<IQueryHandler<TestValidatingQuery, T>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<TestValidatingQuery>(), It.IsAny<CancellationToken>()))
            .Returns(() => inner());

        var sut = new ValidatingQueryDecorator<TestValidatingQuery, T>(
            handler.Object,
            validators,
            NullLogger<ValidatingQueryDecorator<TestValidatingQuery, T>>.Instance);

        return query => sut.HandleAsync(query);
    }
}

// ── Test helpers ──
public sealed record TestValidatingQuery(string SortBy);
