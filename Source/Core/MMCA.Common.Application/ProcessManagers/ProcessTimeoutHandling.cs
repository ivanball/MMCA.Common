namespace MMCA.Common.Application.ProcessManagers;

/// <summary>How <see cref="ProcessTimeoutHandlerBase{TCommand, TProcess, TProcessId}"/> handled one fired timeout.</summary>
public enum ProcessTimeoutHandling
{
    /// <summary>The process no longer exists; nothing was saved.</summary>
    ProcessMissing = 0,

    /// <summary>The aggregate said the timeout does not apply; nothing was saved.</summary>
    NotApplicable = 1,

    /// <summary>The timeout is not due yet (a stale or re-armed row); nothing was saved.</summary>
    NotYetDue = 2,

    /// <summary>The aggregate applied the timeout and the change was saved.</summary>
    Applied = 3,

    /// <summary>The aggregate refused the transition; nothing was saved and the row is not retried.</summary>
    Rejected = 4,

    /// <summary>Another writer moved the process first and the save lost the concurrency race.</summary>
    LostRace = 5,
}
