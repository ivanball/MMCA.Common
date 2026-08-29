namespace MMCA.Common.UI.Common.Interfaces;

/// <summary>
/// How prominent a toast is, and which colour and icon the host renders it with. Mirrors the
/// five levels every component library exposes, so a host can map it one-to-one without losing
/// anything; naming them here is what keeps the vendor's own severity enum out of page code.
/// </summary>
public enum ToastSeverity
{
    /// <summary>Neutral, no colour emphasis.</summary>
    Normal = 0,

    /// <summary>Informational: something happened that the user did not ask for.</summary>
    Info = 1,

    /// <summary>The action the user asked for completed.</summary>
    Success = 2,

    /// <summary>The action completed partially, or completed with something worth knowing.</summary>
    Warning = 3,

    /// <summary>The action failed.</summary>
    Error = 4,
}

/// <summary>
/// Transient user notifications ("toasts"), abstracted away from the component library that draws
/// them. Every page, component and <c>Result</c> helper in the framework talks to this contract, so
/// the vendor type appears in exactly one implementation and a host can swap the renderer (or
/// substitute a recording double in a test) without touching a call site.
/// <para>
/// Registered by <c>AddUIShared</c> over the MudBlazor snackbar. A toast is fire-and-forget by
/// design: nothing here reports whether the message was actually rendered, because during SSR
/// pre-render there is no toast host and the call is a silent no-op.
/// </para>
/// </summary>
public interface IToastService
{
    /// <summary>Raises a success toast: the action the user asked for completed.</summary>
    /// <param name="message">The already-localized sentence to show.</param>
    void Success(string message);

    /// <summary>Raises an informational toast.</summary>
    /// <param name="message">The already-localized sentence to show.</param>
    void Info(string message);

    /// <summary>Raises a warning toast: the action completed, but not cleanly.</summary>
    /// <param name="message">The already-localized sentence to show.</param>
    void Warning(string message);

    /// <summary>Raises an error toast: the action failed.</summary>
    /// <param name="message">The already-localized sentence to show.</param>
    void Error(string message);

    /// <summary>
    /// Raises a toast at a severity chosen at runtime, for the callers (notably
    /// <c>ResultUiExtensions.NotifyOnFailure</c>) that carry the level as a parameter rather than
    /// picking one of the four named methods.
    /// </summary>
    /// <param name="message">The already-localized sentence to show.</param>
    /// <param name="severity">The level to render at.</param>
    void Show(string message, ToastSeverity severity);

    /// <summary>
    /// Raises a two-line toast (an emphasised title above a body) that stays on screen until the
    /// user dismisses it. This is the push-notification shape: the message arrives unprompted, so
    /// it must not expire before the user has looked at the screen.
    /// </summary>
    /// <param name="title">The already-localized headline, rendered emphasised.</param>
    /// <param name="body">The already-localized body text, rendered below the title.</param>
    /// <param name="severity">The level to render at; defaults to <see cref="ToastSeverity.Info"/>.</param>
    void ShowPersistent(string title, string body, ToastSeverity severity = ToastSeverity.Info);
}
