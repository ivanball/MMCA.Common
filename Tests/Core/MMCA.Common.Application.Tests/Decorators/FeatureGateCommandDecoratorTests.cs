using AwesomeAssertions;
using Microsoft.FeatureManagement;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class FeatureGateCommandDecoratorTests
{
    private readonly Mock<IFeatureManager> _featureManager = new();

    // ── Non-feature-gated command passes through unchanged ──
    [Fact]
    public async Task HandleAsync_NonFeatureGatedCommand_DelegatesToInner()
    {
        var inner = new Mock<ICommandHandler<PlainCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

        var sut = new FeatureGateCommandDecorator<PlainCommand, Result>(inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new PlainCommand());

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()), Times.Once);
        _featureManager.Verify(x => x.IsEnabledAsync(It.IsAny<string>()), Times.Never);
    }

    // ── Enabled feature delegates to inner handler ──
    [Fact]
    public async Task HandleAsync_EnabledFeature_DelegatesToInner()
    {
        var inner = new Mock<ICommandHandler<FeatureGatedCommand, Result>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<FeatureGatedCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        _featureManager.Setup(x => x.IsEnabledAsync("TestFeature"))
            .ReturnsAsync(true);

        var sut = new FeatureGateCommandDecorator<FeatureGatedCommand, Result>(inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedCommand());

        result.IsSuccess.Should().BeTrue();
        inner.Verify(x => x.HandleAsync(It.IsAny<FeatureGatedCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Disabled feature returns failure without calling inner handler ──
    [Fact]
    public async Task HandleAsync_DisabledFeature_ReturnsFailure()
    {
        var inner = new Mock<ICommandHandler<FeatureGatedCommand, Result>>();
        _featureManager.Setup(x => x.IsEnabledAsync("TestFeature"))
            .ReturnsAsync(false);

        var sut = new FeatureGateCommandDecorator<FeatureGatedCommand, Result>(inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedCommand());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be("Feature.Disabled");
        inner.Verify(x => x.HandleAsync(It.IsAny<FeatureGatedCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Disabled feature works with generic Result<T> ──
    [Fact]
    public async Task HandleAsync_DisabledFeature_WithGenericResult_ReturnsFailure()
    {
        var inner = new Mock<ICommandHandler<FeatureGatedCommandWithValue, Result<int>>>();
        _featureManager.Setup(x => x.IsEnabledAsync("TestFeature"))
            .ReturnsAsync(false);

        var sut = new FeatureGateCommandDecorator<FeatureGatedCommandWithValue, Result<int>>(
            inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedCommandWithValue());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be("Feature.Disabled");
        inner.Verify(
            x => x.HandleAsync(It.IsAny<FeatureGatedCommandWithValue>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Enabled feature works with generic Result<T> ──
    [Fact]
    public async Task HandleAsync_EnabledFeature_WithGenericResult_DelegatesToInner()
    {
        var inner = new Mock<ICommandHandler<FeatureGatedCommandWithValue, Result<int>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<FeatureGatedCommandWithValue>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<int>.Success(42));
        _featureManager.Setup(x => x.IsEnabledAsync("TestFeature"))
            .ReturnsAsync(true);

        var sut = new FeatureGateCommandDecorator<FeatureGatedCommandWithValue, Result<int>>(
            inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedCommandWithValue());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }
}

// ── Test types ──
public sealed record PlainCommand;

public sealed record FeatureGatedCommand : IFeatureGated
{
    public string FeatureName => "TestFeature";
}

public sealed record FeatureGatedCommandWithValue : IFeatureGated
{
    public string FeatureName => "TestFeature";
}
