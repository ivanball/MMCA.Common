namespace MMCA.Common.Shared.Exceptions;

/// <summary>
/// Thrown when a domain invariant is violated in a context where the <see cref="Abstractions.Result"/>
/// pattern cannot be used (e.g. inside aggregate root constructors called by EF materialization).
/// Prefer returning <see cref="Abstractions.Result"/> with <see cref="Abstractions.Error.Invariant"/>
/// errors for normal business rule violations.
/// </summary>
public class DomainInvariantViolationException : DomainException
{
    /// <summary>Initializes a new instance of the <see cref="DomainInvariantViolationException"/> class.</summary>
    public DomainInvariantViolationException()
        : base() { }

    /// <summary>Initializes a new instance of the <see cref="DomainInvariantViolationException"/> class with a message.</summary>
    /// <param name="message">The invariant violation description.</param>
    public DomainInvariantViolationException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance of the <see cref="DomainInvariantViolationException"/> class with a message and inner exception.</summary>
    /// <param name="message">The invariant violation description.</param>
    /// <param name="innerException">The inner exception.</param>
    public DomainInvariantViolationException(string message, Exception innerException)
        : base(message, innerException) { }
}
