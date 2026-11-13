using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.ProcessManagers;

/// <summary>
/// The decision a process aggregate returns from <c>DecideTimeout</c>. Only
/// <see cref="ProcessTimeoutOutcome.NotYetDue"/> carries <see cref="DueAt"/> and only
/// <see cref="ProcessTimeoutOutcome.Rejected"/> carries <see cref="Errors"/>.
/// </summary>
public sealed record ProcessTimeoutDecision
{
    private ProcessTimeoutDecision(ProcessTimeoutOutcome outcome, DateTime? dueAt, IReadOnlyList<Error> errors)
    {
        Outcome = outcome;
        DueAt = dueAt;
        Errors = errors;
    }

    /// <summary>Gets the outcome.</summary>
    public ProcessTimeoutOutcome Outcome { get; }

    /// <summary>Gets the instant the timeout becomes due, for <see cref="ProcessTimeoutOutcome.NotYetDue"/> only.</summary>
    public DateTime? DueAt { get; }

    /// <summary>Gets the reasons for a <see cref="ProcessTimeoutOutcome.Rejected"/> decision; empty otherwise.</summary>
    public IReadOnlyList<Error> Errors { get; }

    /// <summary>The timeout does not apply to the process.</summary>
    /// <returns>A <see cref="ProcessTimeoutOutcome.NotApplicable"/> decision.</returns>
    public static ProcessTimeoutDecision NotApplicable() => new(ProcessTimeoutOutcome.NotApplicable, null, []);

    /// <summary>The timeout applies but is not due until <paramref name="dueAt"/>.</summary>
    /// <param name="dueAt">The instant the timeout becomes due.</param>
    /// <returns>A <see cref="ProcessTimeoutOutcome.NotYetDue"/> decision.</returns>
    public static ProcessTimeoutDecision NotYetDue(DateTime dueAt) => new(ProcessTimeoutOutcome.NotYetDue, dueAt, []);

    /// <summary>The timeout was applied.</summary>
    /// <returns>An <see cref="ProcessTimeoutOutcome.Applied"/> decision.</returns>
    public static ProcessTimeoutDecision Applied() => new(ProcessTimeoutOutcome.Applied, null, []);

    /// <summary>The aggregate refused the transition.</summary>
    /// <param name="errors">Why; at least one error.</param>
    /// <returns>A <see cref="ProcessTimeoutOutcome.Rejected"/> decision.</returns>
    /// <exception cref="ArgumentException"><paramref name="errors"/> is empty.</exception>
    public static ProcessTimeoutDecision Rejected(IReadOnlyList<Error> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            throw new ArgumentException("A rejected timeout must carry at least one error.", nameof(errors));
        }

        return new(ProcessTimeoutOutcome.Rejected, null, [.. errors]);
    }
}
