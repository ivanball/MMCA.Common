using Microsoft.JSInterop;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Services.Capabilities.Accessibility;
using MudBlazor;

namespace MMCA.Common.UI.Services;

/// <summary>
/// The MudBlazor-backed <see cref="IToastService"/>: one of only two types in the framework that
/// name a component-library service (its sibling is <see cref="MudAppDialogService"/>). Registered
/// by <c>AddUIShared</c>, so every host that calls it gets toasts without any page having to know
/// which library renders them.
/// <para>
/// Every toast is ALSO pushed through <see cref="IAccessibilityAnnouncer"/>. MudBlazor's snackbar
/// host emits no <c>aria-live</c> region of any kind, so a toast is invisible to a screen reader:
/// the one channel the framework uses to tell the user that something happened (a save succeeded, a
/// push notification arrived) reached sighted users only. The announcer writes into the visually
/// hidden live region owned by <c>capabilities-interop.js</c>, which every screen reader monitors.
/// </para>
/// </summary>
internal sealed class MudToastService : IToastService
{
    private readonly ISnackbar _snackbar;
    private readonly IAccessibilityAnnouncer? _announcer;

    /// <summary>
    /// Initializes the service. The announcer is OPTIONAL on purpose: <c>AddCommonUiFacades</c> is
    /// called on its own by the shipped bUnit base and by hosts that never register the device
    /// capabilities, and a toast facade must not become the reason a container fails to resolve.
    /// When it is absent, toasts behave exactly as they did before, so the announcement is a pure
    /// addition rather than a new requirement on the host DI sequence.
    /// </summary>
    public MudToastService(ISnackbar snackbar, IAccessibilityAnnouncer? announcer = null)
    {
        _snackbar = snackbar;
        _announcer = announcer;
    }

    /// <inheritdoc />
    public void Success(string message) => Raise(message, Severity.Success);

    /// <inheritdoc />
    public void Info(string message) => Raise(message, Severity.Info);

    /// <inheritdoc />
    public void Warning(string message) => Raise(message, Severity.Warning);

    /// <inheritdoc />
    public void Error(string message) => Raise(message, Severity.Error);

    /// <inheritdoc />
    public void Show(string message, ToastSeverity severity) => Raise(message, Map(severity));

    /// <inheritdoc />
    public void ShowPersistent(string title, string body, ToastSeverity severity = ToastSeverity.Info)
    {
        _snackbar.Add(
            builder =>
            {
                builder.OpenElement(0, "strong");
                builder.AddContent(1, title);
                builder.CloseElement();
                builder.OpenElement(2, "br");
                builder.CloseElement();
                builder.AddContent(3, body);
            },
            Map(severity),
            options =>
            {
                // The message arrived unprompted (a push), so it must survive until the user has
                // actually looked at the screen rather than expiring on the default timer.
                options.RequireInteraction = true;
                options.SnackbarVariant = Variant.Filled;
            });

        // The rendered toast is two elements; the announcement is one sentence, which is how a
        // screen reader would read the title and body anyway.
        Announce($"{title}. {body}");
    }

    /// <inheritdoc />
    public void ShowAction(
        string message,
        string actionText,
        Func<Task> onAction,
        ToastSeverity severity = ToastSeverity.Info,
        bool requireInteraction = false)
    {
        _snackbar.Add(
            message,
            Map(severity),
            options =>
            {
                options.Action = actionText;
                options.ActionColor = Color.Primary;

                // MudBlazor hands the click a Snackbar instance the callback has no use for: the
                // action is described entirely by the delegate the caller passed.
                options.OnClick = _ => onAction();

                if (requireInteraction)
                {
                    // Stated outright rather than left to MudBlazor's null default (which already
                    // pins an action snackbar open): the contract promises the toast waits, so it
                    // must not depend on a host configuration the caller cannot see. The filled
                    // variant is the same emphasis convention ShowPersistent uses.
                    options.RequireInteraction = true;
                    options.SnackbarVariant = Variant.Filled;
                }
            });

        // The action label is part of what was offered, so it is part of what is announced.
        Announce($"{message}. {actionText}");
    }

    private void Raise(string message, Severity severity)
    {
        _snackbar.Add(message, severity);
        Announce(message);
    }

    /// <summary>
    /// Pushes the toast text into the screen-reader live region without making the caller wait:
    /// <see cref="IToastService"/> is deliberately synchronous (a page raises a toast and carries
    /// on), so the announcement is fire-and-forget. Every failure is swallowed by design: the
    /// browser announcer is a JS-interop call, which throws during prerendering and while a circuit
    /// is tearing down, and a toast must never fail because the announcement could not be delivered.
    /// </summary>
    private void Announce(string message)
    {
        if (_announcer is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _ = AnnounceCoreAsync(message);
    }

    private async Task AnnounceCoreAsync(string message)
    {
        try
        {
            await _announcer!.AnnounceAsync(message).ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone; there is no live region left to write into.
        }
        catch (JSException)
        {
            // The browser rejected the call (no DOM yet, a head with no live region).
        }
        catch (InvalidOperationException)
        {
            // JS interop unavailable (SSR prerender, before hydration), and, through its derived
            // ObjectDisposedException, a scope torn down between the toast and the announcement.
        }
    }

    /// <summary>
    /// Projects the vendor-neutral level onto MudBlazor's own. Written out rather than cast: the
    /// two enums happen to agree numerically today, and an implicit dependency on that would break
    /// silently the day either side gains a member.
    /// </summary>
    private static Severity Map(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Normal => Severity.Normal,
        ToastSeverity.Info => Severity.Info,
        ToastSeverity.Success => Severity.Success,
        ToastSeverity.Warning => Severity.Warning,
        ToastSeverity.Error => Severity.Error,
        _ => Severity.Normal,
    };
}
