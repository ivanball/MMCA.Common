using AwesomeAssertions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class FeatureGateCommandDecoratorTests : FeatureGateDecoratorTestsBase<Result>
{
    protected override string FeatureName => "TestFeature";

    protected override Result SuccessResult => Result.Success();

    // ── Command only: disabled feature works with generic Result<T> ──
    [Fact]
    public async Task HandleAsync_DisabledFeature_WithGenericResult_ReturnsFailure()
    {
        var inner = new Mock<ICommandHandler<FeatureGatedCommandWithValue, Result<int>>>();
        FeatureManager.Setup(x => x.IsEnabledAsync("TestFeature"))
            .ReturnsAsync(false);

        var sut = new FeatureGateCommandDecorator<FeatureGatedCommandWithValue, Result<int>>(
            inner.Object, FeatureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedCommandWithValue());

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be("Feature.Disabled");
        inner.Verify(
            x => x.HandleAsync(It.IsAny<FeatureGatedCommandWithValue>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Command only: enabled feature works with generic Result<T> ──
    [Fact]
    public async Task HandleAsync_EnabledFeature_WithGenericResult_DelegatesToInner()
    {
        var inner = new Mock<ICommandHandler<FeatureGatedCommandWithValue, Result<int>>>();
        inner.Setup(x => x.HandleAsync(It.IsAny<FeatureGatedCommandWithValue>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<int>.Success(42));
        FeatureManager.Setup(x => x.IsEnabledAsync("TestFeature"))
            .ReturnsAsync(true);

        var sut = new FeatureGateCommandDecorator<FeatureGatedCommandWithValue, Result<int>>(
            inner.Object, FeatureManager.Object);

        var result = await sut.HandleAsync(new FeatureGatedCommandWithValue());

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    protected override Func<Task<T>> CreateSut<T>(RequestKind kind, Func<Task<T>> inner) => kind switch
    {
        RequestKind.Plain => Wrap(new PlainCommand(), inner),
        RequestKind.Gated => Wrap(new FeatureGatedCommand(), inner),
        RequestKind.OtherGated => Wrap(new FeatureGatedCommandWithValue(), inner),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private Func<Task<T>> Wrap<TCommand, T>(TCommand command, Func<Task<T>> inner)
        where TCommand : class
    {
        var handler = new Mock<ICommandHandler<TCommand, T>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<TCommand>(), It.IsAny<CancellationToken>()))
            .Returns(() => inner());

        var sut = new FeatureGateCommandDecorator<TCommand, T>(handler.Object, FeatureManager.Object);

        return () => sut.HandleAsync(command);
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
