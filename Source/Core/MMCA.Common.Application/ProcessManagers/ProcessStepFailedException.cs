using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.ProcessManagers;

/// <summary>
/// Thrown by the default <c>OnStepFailed</c> of
/// <see cref="ProcessDomainEventHandlerBase{TDomainEvent, TProcess, TProcessId}"/> when a step returned
/// a failed result, so the outbox redelivers the event.
/// </summary>
public sealed class ProcessStepFailedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ProcessStepFailedException"/> class.</summary>
    public ProcessStepFailedException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProcessStepFailedException"/> class.</summary>
    /// <param name="message">The message.</param>
    public ProcessStepFailedException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProcessStepFailedException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ProcessStepFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProcessStepFailedException"/> class.</summary>
    /// <param name="processType">The process aggregate's type name.</param>
    /// <param name="processId">The process id, formatted with the invariant culture.</param>
    /// <param name="errors">The step's errors.</param>
    public ProcessStepFailedException(string processType, string processId, IReadOnlyList<Error> errors)
        : base(BuildMessage(processType, processId, errors))
    {
        ProcessType = processType;
        ProcessId = processId;
        Errors = errors ?? [];
    }

    /// <summary>Gets the process aggregate's type name, or <see langword="null"/>.</summary>
    public string? ProcessType { get; }

    /// <summary>Gets the process id, or <see langword="null"/>.</summary>
    public string? ProcessId { get; }

    /// <summary>Gets the step's errors; empty when none were given.</summary>
    public IReadOnlyList<Error> Errors { get; } = [];

    private static string BuildMessage(string processType, string processId, IReadOnlyList<Error>? errors)
    {
        var codes = errors is null || errors.Count == 0
            ? "no error code"
            : string.Join(", ", errors.Select(e => e.Code));
        return $"The {processType} process {processId} step failed ({codes}).";
    }
}
