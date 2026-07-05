using AwesomeAssertions;
using Microsoft.JSInterop;
using MMCA.Common.UI.Services.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Verifies <see cref="SameOriginProxyTokenRefresher"/> (the browser-host refresher): the token comes
/// from the JS <c>mmcaAuthSession.getToken</c> fetch bridge, blank results normalize to null, expected
/// JS-interop failures (SSR prerender, disconnected circuit, cancellation) degrade to null, and
/// anything else propagates.
/// </summary>
public sealed class SameOriginProxyTokenRefresherTests
{
    private const string GetTokenIdentifier = "mmcaAuthSession.getToken";

    private readonly Mock<IJSRuntime> _jsRuntime = new();

    private SameOriginProxyTokenRefresher CreateSut() => new(_jsRuntime.Object);

    private void SetupGetToken(string? token) =>
        _jsRuntime
            .Setup(j => j.InvokeAsync<string?>(GetTokenIdentifier, It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ReturnsAsync(token);

    private void SetupGetTokenThrows(Exception exception) =>
        _jsRuntime
            .Setup(j => j.InvokeAsync<string?>(GetTokenIdentifier, It.IsAny<CancellationToken>(), It.IsAny<object?[]?>()))
            .ThrowsAsync(exception);

    [Fact]
    public async Task AcquireAccessTokenAsync_WhenJsReturnsToken_ReturnsIt()
    {
        SetupGetToken("fresh-access-token");

        var result = await CreateSut().AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        result.Should().Be("fresh-access-token");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AcquireAccessTokenAsync_WhenJsReturnsBlank_ReturnsNull(string? token)
    {
        SetupGetToken(token);

        var result = await CreateSut().AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    private static Exception CreateInteropException(string kind) => kind switch
    {
        nameof(InvalidOperationException) => new InvalidOperationException("JS interop is not available during prerendering"),
        nameof(JSDisconnectedException) => new JSDisconnectedException("circuit disconnected"),
        nameof(JSException) => new JSException("getToken failed"),
        nameof(OperationCanceledException) => new OperationCanceledException(),
        nameof(TaskCanceledException) => new TaskCanceledException(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown exception kind."),
    };

    [Theory]
    [InlineData(nameof(InvalidOperationException))]
    [InlineData(nameof(JSDisconnectedException))]
    [InlineData(nameof(JSException))]
    [InlineData(nameof(OperationCanceledException))]
    [InlineData(nameof(TaskCanceledException))]
    public async Task AcquireAccessTokenAsync_WhenJsInteropUnavailable_ReturnsNull(string exceptionKind)
    {
        SetupGetTokenThrows(CreateInteropException(exceptionKind));

        var result = await CreateSut().AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task AcquireAccessTokenAsync_WhenJsThrowsUnexpectedException_Propagates()
    {
        SetupGetTokenThrows(new NotSupportedException("unexpected"));

        var act = () => CreateSut().AcquireAccessTokenAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
