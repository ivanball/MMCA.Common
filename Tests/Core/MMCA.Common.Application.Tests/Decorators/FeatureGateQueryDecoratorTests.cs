using AwesomeAssertions;
using Microsoft.FeatureManagement;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class FeatureGateQueryDecoratorTests
{
    private readonly Mock<IFeatureManager> _featureManager = new();

    // ── Non-feature-gated query passes through unchanged ──
    [Fact]
    public async Task HandleAsync_NonFeatureGatedQuery_DelegatesToInner()
    {
        var inner = new Mock<IQueryHandler<PlainQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<PlainQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<string>.Success("data"));

        var sut = new FeatureGateQueryDecorator<PlainQuery, Result<string>>(inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new PlainQuery());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("data");
        _featureManager.Verify(x => x.IsEnabledAsync(It.IsAny<string>()), Times.Never);
    }

    // ── Enabled feature delegates to inner handler ──
    [Fact]
    public async Task HandleAsync_EnabledFeature_DelegatesToInner()
    {
        var inner = new Mock<IQueryHandler<FeatureGatedQuery, Result<string>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<FeatureGatedQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<string>.Success("data"));
        _featureManager.Setup(x => x.IsEnabledAsync("QueryFeature"))
            .ReturnsAsync(true);

        var sut = new FeatureGateQueryDecorator<FeatureGatedQuery, Result<string>>(
            inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedQuery());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("data");
        inner.Verify(x => x.HandleAsync(It.IsAny<FeatureGatedQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Disabled feature returns failure without calling inner handler ──
    [Fact]
    public async Task HandleAsync_DisabledFeature_ReturnsFailure()
    {
        var inner = new Mock<IQueryHandler<FeatureGatedQuery, Result<string>>>();
        _featureManager.Setup(x => x.IsEnabledAsync("QueryFeature"))
            .ReturnsAsync(false);

        var sut = new FeatureGateQueryDecorator<FeatureGatedQuery, Result<string>>(
            inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedQuery());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be("Feature.Disabled");
        inner.Verify(x => x.HandleAsync(It.IsAny<FeatureGatedQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── A handler whose TResult is neither Result nor Result<T> ──
    // Scrutor's TryDecorate is unconditional, so such a handler gets decorated too. Building the
    // failure delegate eagerly (in the static constructor) turned that into a
    // TypeInitializationException the moment the decorator was RESOLVED, even for a query that
    // never short-circuits.
    [Fact]
    public async Task HandleAsync_NonResultTResult_NonFeatureGatedQuery_PassesThrough()
    {
        var inner = new Mock<IQueryHandler<PlainQuery, string>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<PlainQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("data");

        var sut = new FeatureGateQueryDecorator<PlainQuery, string>(inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new PlainQuery());

        result.Should().Be("data");
        inner.Verify(x => x.HandleAsync(It.IsAny<PlainQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_NonResultTResult_EnabledFeature_PassesThrough()
    {
        var inner = new Mock<IQueryHandler<FeatureGatedQuery, string>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<FeatureGatedQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("data");
        _featureManager.Setup(x => x.IsEnabledAsync("QueryFeature")).ReturnsAsync(true);

        var sut = new FeatureGateQueryDecorator<FeatureGatedQuery, string>(inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedQuery());

        result.Should().Be("data");
    }

    [Fact]
    public async Task HandleAsync_NonResultTResult_DisabledFeature_FailsOnlyOnTheShortCircuit()
    {
        var inner = new Mock<IQueryHandler<FeatureGatedQueryNonGeneric, string>>();
        _featureManager.Setup(x => x.IsEnabledAsync("QueryFeature")).ReturnsAsync(false);

        var sut = new FeatureGateQueryDecorator<FeatureGatedQueryNonGeneric, string>(
            inner.Object, _featureManager.Object);

        // Fabricating a failure is genuinely impossible for this TResult, so the short-circuit path
        // still fails: as the factory's own InvalidOperationException, at the point of use.
        Func<Task> act = () => sut.HandleAsync(new FeatureGatedQueryNonGeneric());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Disabled feature with non-generic Result ──
    [Fact]
    public async Task HandleAsync_DisabledFeature_WithNonGenericResult_ReturnsFailure()
    {
        var inner = new Mock<IQueryHandler<FeatureGatedQueryNonGeneric, Result>>();
        _featureManager.Setup(x => x.IsEnabledAsync("QueryFeature"))
            .ReturnsAsync(false);

        var sut = new FeatureGateQueryDecorator<FeatureGatedQueryNonGeneric, Result>(
            inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedQueryNonGeneric());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be("Feature.Disabled");
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
