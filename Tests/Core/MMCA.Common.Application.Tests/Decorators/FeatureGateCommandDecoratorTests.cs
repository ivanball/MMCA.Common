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

    // ── A handler whose TResult is neither Result nor Result<T> ──
    // Scrutor's TryDecorate is unconditional, so such a handler gets decorated too. Building the
    // failure delegate eagerly (in the static constructor) turned that into a
    // TypeInitializationException the moment the decorator was RESOLVED, even for a command that
    // never short-circuits.
    [Fact]
    public async Task HandleAsync_NonResultTResult_NonFeatureGatedCommand_PassesThrough()
    {
        var inner = new Mock<ICommandHandler<PlainCommand, string>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("handled");

        var sut = new FeatureGateCommandDecorator<PlainCommand, string>(inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new PlainCommand());

        result.Should().Be("handled");
        inner.Verify(x => x.HandleAsync(It.IsAny<PlainCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_NonResultTResult_EnabledFeature_PassesThrough()
    {
        var inner = new Mock<ICommandHandler<FeatureGatedCommand, string>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<FeatureGatedCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("handled");
        _featureManager.Setup(x => x.IsEnabledAsync("TestFeature")).ReturnsAsync(true);

        var sut = new FeatureGateCommandDecorator<FeatureGatedCommand, string>(inner.Object, _featureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedCommand());

        result.Should().Be("handled");
    }

    [Fact]
    public async Task HandleAsync_NonResultTResult_DisabledFeature_FailsOnlyOnTheShortCircuit()
    {
        var inner = new Mock<ICommandHandler<FeatureGatedCommandWithValue, string>>();
        _featureManager.Setup(x => x.IsEnabledAsync("TestFeature")).ReturnsAsync(false);

        var sut = new FeatureGateCommandDecorator<FeatureGatedCommandWithValue, string>(
            inner.Object, _featureManager.Object);

        // Fabricating a failure is genuinely impossible for this TResult, so the short-circuit path
        // still fails: as the factory's own InvalidOperationException, at the point of use.
        Func<Task> act = () => sut.HandleAsync(new FeatureGatedCommandWithValue());

        await act.Should().ThrowAsync<InvalidOperationException>();
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
