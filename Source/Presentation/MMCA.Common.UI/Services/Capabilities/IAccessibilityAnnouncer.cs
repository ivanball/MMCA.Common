namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>
/// Pushes a spoken announcement to the platform screen reader (TalkBack/VoiceOver via MAUI
/// <c>SemanticScreenReader</c>; an <c>aria-live</c> region in browsers) for events a sighted
/// user perceives visually — a live poll opening, a question being answered, the unread badge
/// incrementing. Silent no-op when no assistive technology is active.
/// </summary>
public interface IAccessibilityAnnouncer
{
    /// <summary>Announces <paramref name="message"/> politely (does not interrupt current speech).</summary>
    Task AnnounceAsync(string message, CancellationToken cancellationToken = default);
}
