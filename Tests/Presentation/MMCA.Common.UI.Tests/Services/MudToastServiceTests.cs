using AwesomeAssertions;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// Unit tests for <see cref="MudToastService"/>, the one place in the framework that names
/// MudBlazor's <see cref="ISnackbar"/>. The interesting surface is <c>ShowAction</c>: it is the only
/// method that configures the snackbar rather than just handing it a message, so what it writes into
/// the options lambda is the whole behaviour. The lambda is captured off the mock and invoked against
/// a real <see cref="SnackbarOptions"/>, which is what MudBlazor itself would do.
/// </summary>
public sealed class MudToastServiceTests
{
    private readonly Mock<ISnackbar> _snackbar = new();

    private MudToastService Toast => new(_snackbar.Object);

    [Fact]
    public void ShowAction_RendersALabelledActionButtonOnThePrimaryColour()
    {
        var options = CaptureOptions(toast =>
            toast.ShowAction("Item deleted", "Undo", () => Task.CompletedTask));

        options.Action.Should().Be("Undo");
        options.ActionColor.Should().Be(Color.Primary);
    }

    [Fact]
    public async Task ShowAction_RunsTheCallbackWhenTheToastIsClicked()
    {
        var ran = false;
        var options = CaptureOptions(toast =>
            toast.ShowAction("Item deleted", "Undo", () =>
            {
                ran = true;
                return Task.CompletedTask;
            }));

        options.OnClick.Should().NotBeNull();
        await options.OnClick(null!);

        ran.Should().BeTrue();
    }

    [Fact]
    public async Task ShowAction_DoesNotSwallowACallbackFailure()
    {
        // Documented contract: nothing wraps the callback, so a caller whose work can fail guards it
        // itself instead of discovering the failure as a swallowed no-op.
        var options = CaptureOptions(toast =>
            toast.ShowAction("Item deleted", "Undo", () => throw new InvalidOperationException("boom")));

        var act = async () => await options.OnClick!(null!);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void ShowAction_WithoutRequireInteraction_LeavesTheHostTimerDefaultsAlone()
    {
        var options = CaptureOptions(toast =>
            toast.ShowAction("Item deleted", "Undo", () => Task.CompletedTask));

        // Untouched, not written false: the host's snackbar configuration decides how long an
        // ordinary action toast lives.
        options.RequireInteraction.Should().BeNull();
    }

    [Fact]
    public void ShowAction_WithRequireInteraction_PinsTheToastOpenAndFillsIt()
    {
        var options = CaptureOptions(toast =>
            toast.ShowAction("Session expiring", "Stay signed in", () => Task.CompletedTask, requireInteraction: true));

        options.RequireInteraction.Should().BeTrue();
        options.SnackbarVariant.Should().Be(Variant.Filled);
        options.Action.Should().Be("Stay signed in");
    }

    [Theory]
    [InlineData(ToastSeverity.Normal, Severity.Normal)]
    [InlineData(ToastSeverity.Info, Severity.Info)]
    [InlineData(ToastSeverity.Success, Severity.Success)]
    [InlineData(ToastSeverity.Warning, Severity.Warning)]
    [InlineData(ToastSeverity.Error, Severity.Error)]
    public void ShowAction_MapsTheVendorNeutralSeverity(ToastSeverity severity, Severity expected)
    {
        Toast.ShowAction("Message", "Action", () => Task.CompletedTask, severity);

        _snackbar.Verify(
            s => s.Add("Message", expected, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public void ShowAction_PassesTheMessageThroughUnchanged()
    {
        Toast.ShowAction("Item deleted", "Undo", () => Task.CompletedTask);

        _snackbar.Verify(
            s => s.Add("Item deleted", Severity.Info, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// Runs <paramref name="act"/> against the service, captures the options lambda it handed the
    /// snackbar, and applies it to a fresh <see cref="SnackbarOptions"/> carrying MudBlazor's own
    /// defaults, so an assertion sees exactly what the rendered snackbar would.
    /// </summary>
    private SnackbarOptions CaptureOptions(Action<MudToastService> act)
    {
        Action<SnackbarOptions>? configure = null;
        _snackbar
            .Setup(s => s.Add(
                It.IsAny<string>(),
                It.IsAny<Severity>(),
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()))
            .Callback<string, Severity, Action<SnackbarOptions>, string>((_, _, cfg, _) => configure = cfg);

        act(Toast);

        configure.Should().NotBeNull("the service must configure the snackbar it raises");

        var options = new SnackbarOptions(Severity.Info, new SnackbarConfiguration());
        configure(options);
        return options;
    }
}
