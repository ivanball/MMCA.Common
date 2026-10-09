using MMCA.Common.UI.Common.Interfaces;
using MudBlazor;

namespace MMCA.Common.UI.Services;

/// <summary>
/// The MudBlazor-backed <see cref="IToastService"/>: one of only two types in the framework that
/// name a component-library service (its sibling is <see cref="MudAppDialogService"/>). Registered
/// by <c>AddUIShared</c>, so every host that calls it gets toasts without any page having to know
/// which library renders them.
/// <para>
/// Screen-reader announcement is NOT this service's job: <c>MmcaThemeProviders</c> hosts
/// <c>MudSnackbarProvider</c> inside a <c>role="status" aria-live="polite"</c> element, so the
/// rendered toast IS the live-region content. Pushing the text through a second channel would put
/// the same sentence in the DOM twice, which reads the message twice and makes every text locator
/// ambiguous.
/// </para>
/// <para>
/// Pinned toasts (<see cref="ShowPersistent"/>, and <see cref="ShowAction"/> with
/// <c>requireInteraction</c>) never expire, and MudBlazor displays at most
/// <see cref="SnackbarConfiguration.MaxDisplayedSnackbars"/> at once, queueing the rest out of
/// sight. Left alone, a burst of pushes fills every slot with pinned toasts and each newer one
/// waits invisibly until the user closes an older one. So before raising a pinned toast, the
/// service removes the oldest pinned toast it raised itself once those have reached the cap,
/// and the newest is always the one on screen. Only toasts this service added are ever
/// removed; timed toasts and snackbars raised elsewhere are untouched.
/// </para>
/// <para>
/// Registered scoped, so the tracking is per circuit (Server) or per app (WebAssembly), which is
/// per user session. Snackbar close events arrive on timer threads, hence the lock.
/// </para>
/// </summary>
internal sealed class MudToastService : IToastService
{
    private readonly ISnackbar _snackbar;

    // Pinned toasts raised here and not yet closed, oldest first.
    private readonly List<Snackbar> _pinned = [];
    private readonly Lock _pinnedSync = new();

    public MudToastService(ISnackbar snackbar) => _snackbar = snackbar;

    /// <inheritdoc />
    public void Success(string message) => _snackbar.Add(message, Severity.Success);

    /// <inheritdoc />
    public void Info(string message) => _snackbar.Add(message, Severity.Info);

    /// <inheritdoc />
    public void Warning(string message) => _snackbar.Add(message, Severity.Warning);

    /// <inheritdoc />
    public void Error(string message) => _snackbar.Add(message, Severity.Error);

    /// <inheritdoc />
    public void Show(string message, ToastSeverity severity) => _snackbar.Add(message, Map(severity));

    /// <inheritdoc />
    public void ShowPersistent(string title, string body, ToastSeverity severity = ToastSeverity.Info) =>
        AddPinned(() => _snackbar.Add(
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
            }));

    /// <inheritdoc />
    public void ShowAction(
        string message,
        string actionText,
        Func<Task> onAction,
        ToastSeverity severity = ToastSeverity.Info,
        bool requireInteraction = false)
    {
        if (requireInteraction)
        {
            AddPinned(() => _snackbar.Add(message, Map(severity), Configure));
        }
        else
        {
            _snackbar.Add(message, Map(severity), Configure);
        }

        void Configure(SnackbarOptions options)
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
        }
    }

    /// <summary>
    /// Raises a pinned toast through <paramref name="add"/>, first removing the oldest pinned toast
    /// this service raised while those still open have reached the display cap, so the new one is
    /// visible at once instead of queueing behind toasts that never expire.
    /// </summary>
    private void AddPinned(Func<Snackbar?> add)
    {
        lock (_pinnedSync)
        {
            if (_pinned.Count > 0)
            {
                ForgetRemovedPinned();

                var cap = _snackbar.Configuration.MaxDisplayedSnackbars;
                while (_pinned.Count > 0 && _pinned.Count >= cap)
                {
                    var oldest = _pinned[0];
                    _pinned.RemoveAt(0);
                    oldest.OnClose -= ForgetPinned;
                    _snackbar.Remove(oldest);
                }
            }

            // Null when the host's duplicate prevention suppressed the toast: nothing to track.
            var snackbar = add();
            if (snackbar is not null)
            {
                snackbar.OnClose += ForgetPinned;
                _pinned.Add(snackbar);
            }
        }
    }

    /// <summary>
    /// A user close (or any timer-driven close) raises <see cref="Snackbar.OnClose"/>, which
    /// <see cref="ForgetPinned"/> handles. <c>ISnackbar.Remove</c>, <c>RemoveByKey</c> and
    /// <c>Clear</c> raise nothing, so this catches those: while fewer snackbars are displayed than
    /// the cap, nothing is queued, and a tracked toast that is not displayed is gone.
    /// </summary>
    private void ForgetRemovedPinned()
    {
        var shown = _snackbar.ShownSnackbars.ToList();
        if (shown.Count < _snackbar.Configuration.MaxDisplayedSnackbars)
        {
            _pinned.RemoveAll(snackbar => !shown.Contains(snackbar));
        }
    }

    private void ForgetPinned(Snackbar snackbar)
    {
        lock (_pinnedSync)
        {
            _pinned.Remove(snackbar);
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
