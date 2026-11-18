using AwesomeAssertions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class FeatureGateQueryDecoratorTests : FeatureGateDecoratorTestsBase<Result<string>>
{
    protected override string FeatureName => "QueryFeature";

    protected override Result<string> SuccessResult => Result<string>.Success("data");

    // ── Query only: disabled feature with non-generic Result ──
    [Fact]
    public async Task HandleAsync_DisabledFeature_WithNonGenericResult_ReturnsFailure()
    {
        var inner = new Mock<IQueryHandler<FeatureGatedQueryNonGeneric, Result>>();
        FeatureManager.Setup(x => x.IsEnabledAsync("QueryFeature"))
            .ReturnsAsync(false);

        var sut = new FeatureGateQueryDecorator<FeatureGatedQueryNonGeneric, Result>(
            inner.Object, FeatureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedQueryNonGeneric());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be("Feature.Disabled");
    }

    protected override void AssertPassedThrough(Result<string> result)
    {
        base.AssertPassedThrough(result);
        result.Value.Should().Be("data");
    }

    protected override Func<Task<T>> CreateSut<T>(RequestKind kind, Func<Task<T>> inner) => kind switch
    {
        RequestKind.Plain => Wrap(new PlainQuery(), inner),
        RequestKind.Gated => Wrap(new FeatureGatedQuery(), inner),
        RequestKind.OtherGated => Wrap(new FeatureGatedQueryNonGeneric(), inner),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private Func<Task<T>> Wrap<TQuery, T>(TQuery query, Func<Task<T>> inner)
        where TQuery : class
    {
        var handler = new Mock<IQueryHandler<TQuery, T>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<TQuery>(), It.IsAny<CancellationToken>()))
            .Returns(() => inner());

        var sut = new FeatureGateQueryDecorator<TQuery, T>(handler.Object, FeatureManager.Object);

        return () => sut.HandleAsync(query);
    }
}

// ── Test types ──
public sealed record PlainQuery;

public sealed record FeatureGatedQuery : IFeatureGated
{
    public string FeatureName => "QueryFeature";
}

public sealed record FeatureGatedQueryNonGeneric : IFeatureGated
{
    public string FeatureName => "QueryFeature";
}
