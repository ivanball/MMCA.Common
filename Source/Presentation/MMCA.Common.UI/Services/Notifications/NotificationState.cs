namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// Shared state for notification unread count. Components subscribe to <see cref="OnChange"/>
/// to react when the count changes (e.g., new notification received, notification marked as read).
/// Registered as scoped so each Blazor circuit gets its own instance.
/// </summary>
public sealed class NotificationState
{
    /// <summary>Gets the current unread notification count.</summary>
    public int UnreadCount { get; private set; }

    /// <summary>Raised when <see cref="UnreadCount"/> changes.</summary>
    public event EventHandler? OnChange;

    /// <summary>Sets the unread count to an absolute value (e.g., after fetching from API).</summary>
    public void SetUnreadCount(int count)
    {
        if (UnreadCount == count)
        {
            return;
        }

        UnreadCount = count;
        OnChange?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Increments the unread count by one (e.g., real-time notification received).</summary>
    public void IncrementUnreadCount()
    {
        UnreadCount++;
        OnChange?.Invoke(this, EventArgs.Empty);
    }
}
