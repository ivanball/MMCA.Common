using AwesomeAssertions;
using Microsoft.FeatureManagement;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// The feature-gate decorator contract shared by the command and the query pipeline: an ungated
/// request never consults the feature manager, an enabled flag delegates, a disabled flag returns
/// Feature.Disabled with a generic message and never reaches the handler, and a non-Result handler
/// resolves and fails only on the short-circuit. Each subclass supplies the decorator under test and
/// its request types.
/// </summary>
/// <typeparam name="TResult">The result type the subclass's handlers return.</typeparam>
public abstract class FeatureGateDecoratorTestsBase<TResult>
    where TResult : Result
{
    /// <summary>Which request type the subclass builds the decorator for.</summary>
    protected enum RequestKind
    {
        /// <summary>A request with no feature gate.</summary>
        Plain,

        /// <summary>A request gated on <see cref="FeatureName"/>.</summary>
        Gated,

        /// <summary>A second request type gated on the same flag, used by the short-circuit cases.</summary>
        OtherGated,
    }

    /// <summary>The feature manager every decorator under test consults.</summary>
    protected Mock<IFeatureManager> FeatureManager { get; } = new();

    /// <summary>The flag the gated request types declare.</summary>
    protected abstract string FeatureName { get; }

    /// <summary>The successful result the inner handler returns.</summary>
    protected abstract TResult SuccessResult { get; }

    /// <summary>
    /// Builds the decorator over an inner handler that answers through <paramref name="inner"/> and
    /// the shared <see cref="FeatureManager"/>, and returns a call that sends it one request of
    /// <paramref name="kind"/>.
    /// </summary>
    /// <typeparam name="T">The handler's result type (a non-Result type exercises the lazy factory).</typeparam>
    protected abstract Func<Task<T>> CreateSut<T>(RequestKind kind, Func<Task<T>> inner);

    /// <summary>Extra checks on a successful pass-through (the query also checks the value).</summary>
    protected virtual void AssertPassedThrough(TResult result) => result.IsSuccess.Should().BeTrue();

    // ── Non-feature-gated request passes through unchanged ──
    [Fact]
    public async Task HandleAsync_NonFeatureGatedRequest_DelegatesToInner()
    {
        var calls = 0;
        var handle = CreateSut(RequestKind.Plain, () => Count(ref calls, SuccessResult));

        var result = await handle();

        AssertPassedThrough(result);
        calls.Should().Be(1);
        FeatureManager.Verify(x => x.IsEnabledAsync(It.IsAny<string>()), Times.Never);
    }

    // ── Enabled feature delegates to inner handler ──
    [Fact]
    public async Task HandleAsync_EnabledFeature_DelegatesToInner()
    {
        var calls = 0;
        FeatureManager.Setup(x => x.IsEnabledAsync(FeatureName))
            .ReturnsAsync(true);
        var handle = CreateSut(RequestKind.Gated, () => Count(ref calls, SuccessResult));

        var result = await handle();

        AssertPassedThrough(result);
        calls.Should().Be(1);
    }

    // ── Disabled feature returns failure without calling inner handler ──
    [Fact]
    public async Task HandleAsync_DisabledFeature_ReturnsFailure()
    {
        var calls = 0;
        FeatureManager.Setup(x => x.IsEnabledAsync(FeatureName))
            .ReturnsAsync(false);
        var handle = CreateSut(RequestKind.Gated, () => Count(ref calls, SuccessResult));

        var result = await handle();

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be("Feature.Disabled");
        calls.Should().Be(0);
    }

    // ── The user-facing message (X-28 side, ADC local test run 6) ──
    // The failure message reaches the user verbatim (the UI shows a server-phrased message), so it
    // must not leak the internal flag name and must match the wording DisabledFeatureHandler already
    // uses for a [FeatureGate] endpoint. The CODE stays Feature.Disabled for clients that branch on it.
    [Fact]
    public async Task HandleAsync_DisabledFeature_UsesTheGenericMessage_WithoutTheFlagName()
    {
        FeatureManager.Setup(x => x.IsEnabledAsync(FeatureName))
            .ReturnsAsync(false);
        var handle = CreateSut(RequestKind.Gated, () => Task.FromResult(SuccessResult));

        var result = await handle();

        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Feature.Disabled");
        error.Message.Should().NotContain(FeatureName, "the flag name is internal and must not reach the user");
        error.Message.Should().Be("The requested feature is not currently available.");
    }

    // ── A handler whose TResult is neither Result nor Result<T> ──
    // Scrutor's TryDecorate is unconditional, so such a handler gets decorated too. Building the
    // failure delegate eagerly (in the static constructor) turned that into a
    // TypeInitializationException the moment the decorator was RESOLVED, even for a request that
    // never short-circuits.
    [Fact]
    public async Task HandleAsync_NonResultTResult_NonFeatureGatedRequest_PassesThrough()
    {
        var calls = 0;
        var handle = CreateSut(RequestKind.Plain, () => Count(ref calls, "handled"));

        var result = await handle();

        result.Should().Be("handled");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_NonResultTResult_EnabledFeature_PassesThrough()
    {
        FeatureManager.Setup(x => x.IsEnabledAsync(FeatureName)).ReturnsAsync(true);
        var handle = CreateSut(RequestKind.Gated, () => Task.FromResult("handled"));

        var result = await handle();

        result.Should().Be("handled");
    }

    [Fact]
    public async Task HandleAsync_NonResultTResult_DisabledFeature_FailsOnlyOnTheShortCircuit()
    {
        FeatureManager.Setup(x => x.IsEnabledAsync(FeatureName)).ReturnsAsync(false);
        var handle = CreateSut(RequestKind.OtherGated, () => Task.FromResult("handled"));

        // Fabricating a failure is genuinely impossible for this TResult, so the short-circuit path
        // still fails: as the factory's own InvalidOperationException, at the point of use.
        Func<Task> act = () => handle();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static Task<T> Count<T>(ref int calls, T result)
    {
        calls++;
        return Task.FromResult(result);
    }
}
