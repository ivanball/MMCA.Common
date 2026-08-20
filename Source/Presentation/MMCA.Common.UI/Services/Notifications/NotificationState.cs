namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// Shared state for notification unread count. Components subscribe to <see cref="OnChange"/>
/// to react when the count changes (e.g., new notification received, notification marked as read).
/// Registered as scoped so each Blazor circuit gets its own instance.
/// </summary>
public sealed class NotificationState
{
    private readonly Lock _pollerSync = new();

    /// <summary>
    /// The component that currently holds the single active-poller slot, or <see langword="null"/>
    /// when the slot is free. An owner reference (rather than a counter) is what makes register and
    /// unregister symmetric: a counter leaks one increment per teardown that never unregisters, and
    /// once it leaks no bell can ever win the slot again for the life of the circuit.
    /// </summary>
    private object? _pollerOwner;

    /// <summary>Gets the current unread notification count.</summary>
    public int UnreadCount { get; private set; }

    /// <summary>Raised when <see cref="UnreadCount"/> changes.</summary>
    public event EventHandler? OnChange;

    /// <summary>
    /// Raised when a real-time notification arrives and the badge should refresh from the API.
    /// Subscribers (e.g., <c>NotificationBell</c>) use this to fetch the authoritative count.
    /// </summary>
    public event EventHandler? OnRefreshRequested;

    /// <summary>
    /// Raised when the active-poller slot becomes free. A surviving <c>NotificationBell</c> uses this
    /// to take over polling when the bell that held the slot is torn down (the desktop and mobile
    /// placements are rebuilt independently whenever the authentication state changes).
    /// </summary>
    public event EventHandler? OnPollerSlotFreed;

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

    /// <summary>Signals that a real-time notification arrived and the count should be refreshed from the API.</summary>
    public void RequestRefresh() => OnRefreshRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Claims the single active-poller slot for <paramref name="owner"/>. Returns
    /// <see langword="true"/> when the caller now holds the slot (including a caller that already
    /// held it, so the call is idempotent) and should poll; <see langword="false"/> when another
    /// owner holds it, which is how duplicate <c>NotificationBell</c> placements avoid double-polling.
    /// </summary>
    /// <param name="owner">The claiming component instance.</param>
    public bool TryRegisterPoller(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        lock (_pollerSync)
        {
            if (_pollerOwner is not null && !ReferenceEquals(_pollerOwner, owner))
            {
                return false;
            }

            _pollerOwner = owner;
            return true;
        }
    }

    /// <summary>
    /// Releases the active-poller slot, but only when <paramref name="owner"/> is the component that
    /// holds it, so a non-owner disposing cannot evict the live poller. Freeing the slot raises
    /// <see cref="OnPollerSlotFreed"/> so a surviving bell can take polling over.
    /// </summary>
    /// <param name="owner">The component releasing the slot.</param>
    public void UnregisterPoller(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        lock (_pollerSync)
        {
            if (!ReferenceEquals(_pollerOwner, owner))
            {
                return;
            }

            _pollerOwner = null;
        }

        // Raised outside the lock: a subscriber claims the slot from its handler, which would
        // otherwise re-enter the lock on the disposing component's thread.
        OnPollerSlotFreed?.Invoke(this, EventArgs.Empty);
    }
}
