namespace MMCA.Common.UI.Services.Offline;

/// <summary>How a queued write left the <see cref="IOfflineWriteQueue"/>.</summary>
public enum OfflineWriteOutcome
{
    /// <summary>The replay succeeded.</summary>
    Completed,

    /// <summary>The server refused the replay because the item changed (a conflict, for example a 412).</summary>
    Conflict,

    /// <summary>The server refused the replay for any other permanent reason.</summary>
    Rejected,

    /// <summary>The write was older than <see cref="OfflineWriteQueueOptions.MaxAge"/> and was discarded unsent.</summary>
    Expired,
}
