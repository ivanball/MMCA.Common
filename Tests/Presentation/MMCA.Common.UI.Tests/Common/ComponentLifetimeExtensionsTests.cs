using AwesomeAssertions;
using MMCA.Common.UI.Common;

namespace MMCA.Common.UI.Tests.Common;

/// <summary>
/// The component-lifetime token read: live while the source is, already cancelled once the component
/// has cancelled or disposed it, and never an <see cref="ObjectDisposedException"/>, which is what a
/// raw <c>_cts.Token</c> read threw when a load resumed after the user navigated away.
/// </summary>
public sealed class ComponentLifetimeExtensionsTests
{
    [Fact]
    public void LiveSource_ReturnsItsOwnToken()
    {
        using var source = new CancellationTokenSource();

        var token = source.LifetimeToken();

        token.Should().Be(source.Token);
        token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void CancelledThenDisposedSource_ReturnsACancelledToken_WithoutThrowing()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        source.Dispose();

        var read = () => source.LifetimeToken();

        read.Should().NotThrow().Which.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void DisposedWithoutCancellingSource_ReturnsACancelledToken_WithoutThrowing()
    {
        var source = new CancellationTokenSource();
        source.Dispose();

        var read = () => source.LifetimeToken();

        read.Should().NotThrow().Which.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void NullSource_IsTreatedAsEnded()
    {
        CancellationTokenSource? source = null;

        source.LifetimeToken().IsCancellationRequested.Should().BeTrue();
    }
}
