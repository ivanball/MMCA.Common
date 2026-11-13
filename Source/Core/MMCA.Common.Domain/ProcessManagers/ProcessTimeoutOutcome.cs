namespace MMCA.Common.Domain.ProcessManagers;

/// <summary>What a process aggregate decided about a fired timeout.</summary>
public enum ProcessTimeoutOutcome
{
    /// <summary>The timeout does not apply to the process in its current state (or by its name).</summary>
    NotApplicable = 0,

    /// <summary>The timeout applies but is not due yet; a stale or re-armed row fired early.</summary>
    NotYetDue = 1,

    /// <summary>The timeout was applied and the aggregate transitioned.</summary>
    Applied = 2,

    /// <summary>The aggregate refused the transition the timeout asked for.</summary>
    Rejected = 3,
}
