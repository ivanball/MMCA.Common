namespace MMCA.Common.Shared.Exceptions;

/// <summary>
/// Base class for domain-layer exceptions. In most cases, prefer the <see cref="Abstractions.Result"/>
/// pattern for expected error paths. Reserve exceptions for truly exceptional situations
/// (e.g. programming errors, corrupted state). Caught by <c>DomainExceptionHandler</c> middleware
/// and converted to Problem Details responses.
/// </summary>
public abstract class DomainException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DomainException"/> class.</summary>
    protected DomainException() { }

    /// <summary>Initializes a new instance of the <see cref="DomainException"/> class with a message.</summary>
    /// <param name="message">The error message.</param>
    protected DomainException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance of the <see cref="DomainException"/> class with a message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    protected DomainException(string message, Exception innerException)
        : base(message, innerException) { }
}
