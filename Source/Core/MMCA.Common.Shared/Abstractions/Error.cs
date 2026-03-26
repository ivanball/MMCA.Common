namespace MMCA.Common.Shared.Abstractions;

/// <summary>
/// Immutable error value carried by <see cref="Result"/> and <see cref="Result{T}"/>.
/// Each error has a machine-readable <see cref="Code"/>, a human-readable <see cref="Message"/>,
/// and an <see cref="ErrorType"/> that determines the HTTP status code when the error
/// reaches <c>ApiControllerBase</c>. Factory methods enforce the correct <see cref="ErrorType"/>
/// so callers never need to specify it manually.
/// </summary>
/// <param name="Code">Machine-readable error code (e.g. "Order.NotFound"). Used for programmatic error handling.</param>
/// <param name="Message">Human-readable description suitable for API consumers.</param>
/// <param name="Type">Error classification that drives HTTP status code mapping.</param>
/// <param name="Source">Optional origin context, typically the method or operation that produced the error.</param>
/// <param name="Target">Optional target context, typically the field or entity the error relates to.</param>
public record Error(
    string Code,
    string Message,
    ErrorType Type,
    string? Source = null,
    string? Target = null)
{
    /// <summary>Generic not-found error for use when a specific entity error code is unnecessary.</summary>
    public static readonly Error NotFound = NotFoundError("Error.NotFound", "Entity not found");

    /// <summary>Conflict error indicating the target entity was already soft-deleted.</summary>
    public static readonly Error AlreadyDeleted = Conflict("Error.AlreadyDeleted", "The entity has already been deleted");

    /// <summary>Validation error for an unrecognized or unsupported entity field name (e.g. in field projection or sorting).</summary>
    public static readonly Error InvalidEntityField = Validation("Error.InvalidEntityField", "The provided entity field isn't valid");

    /// <summary>Creates a <see cref="ErrorType.Validation"/> error for input/request validation failures.</summary>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="source">Optional origin context.</param>
    /// <param name="target">Optional target field or entity.</param>
    /// <returns>A new <see cref="Error"/> with <see cref="ErrorType.Validation"/>.</returns>
    public static Error Validation(string code, string message, string? source = null, string? target = null) =>
        new(code, message, ErrorType.Validation, source, target);

    /// <summary>Creates an <see cref="ErrorType.Invariant"/> error for domain business rule violations.</summary>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="source">Optional origin context.</param>
    /// <param name="target">Optional target field or entity.</param>
    /// <returns>A new <see cref="Error"/> with <see cref="ErrorType.Invariant"/>.</returns>
    public static Error Invariant(string code, string message, string? source = null, string? target = null) =>
        new(code, message, ErrorType.Invariant, source, target);

    /// <summary>Creates a <see cref="ErrorType.NotFound"/> error for missing entities.</summary>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="source">Optional origin context.</param>
    /// <param name="target">Optional target field or entity.</param>
    /// <returns>A new <see cref="Error"/> with <see cref="ErrorType.NotFound"/>.</returns>
    public static Error NotFoundError(string code, string message, string? source = null, string? target = null) =>
        new(code, message, ErrorType.NotFound, source, target);

    /// <summary>Creates a <see cref="ErrorType.Conflict"/> error for state conflicts (e.g. duplicates, already deleted).</summary>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="source">Optional origin context.</param>
    /// <param name="target">Optional target field or entity.</param>
    /// <returns>A new <see cref="Error"/> with <see cref="ErrorType.Conflict"/>.</returns>
    public static Error Conflict(string code, string message, string? source = null, string? target = null) =>
        new(code, message, ErrorType.Conflict, source, target);

    /// <summary>Creates an <see cref="ErrorType.Unauthorized"/> error when the caller is not authenticated.</summary>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="source">Optional origin context.</param>
    /// <param name="target">Optional target field or entity.</param>
    /// <returns>A new <see cref="Error"/> with <see cref="ErrorType.Unauthorized"/>.</returns>
    public static Error Unauthorized(string code, string message, string? source = null, string? target = null) =>
        new(code, message, ErrorType.Unauthorized, source, target);

    /// <summary>Creates a <see cref="ErrorType.Forbidden"/> error when the caller lacks permission.</summary>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="source">Optional origin context.</param>
    /// <param name="target">Optional target field or entity.</param>
    /// <returns>A new <see cref="Error"/> with <see cref="ErrorType.Forbidden"/>.</returns>
    public static Error Forbidden(string code, string message, string? source = null, string? target = null) =>
        new(code, message, ErrorType.Forbidden, source, target);

    /// <summary>Creates an <see cref="ErrorType.UnprocessableEntity"/> error for semantically invalid requests (e.g. immutable field changes).</summary>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="source">Optional origin context.</param>
    /// <param name="target">Optional target field or entity.</param>
    /// <returns>A new <see cref="Error"/> with <see cref="ErrorType.UnprocessableEntity"/>.</returns>
    public static Error UnprocessableEntity(string code, string message, string? source = null, string? target = null) =>
        new(code, message, ErrorType.UnprocessableEntity, source, target);

    /// <summary>Creates a general <see cref="ErrorType.Failure"/> error for unclassified failures.</summary>
    /// <param name="code">Machine-readable error code.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="source">Optional origin context.</param>
    /// <param name="target">Optional target field or entity.</param>
    /// <returns>A new <see cref="Error"/> with <see cref="ErrorType.Failure"/>.</returns>
    public static Error Failure(string code, string message, string? source = null, string? target = null) =>
        new(code, message, ErrorType.Failure, source, target);

    /// <summary>Returns a copy of this error with the specified <paramref name="source"/>.</summary>
    /// <param name="source">The origin context to attach.</param>
    /// <returns>A new <see cref="Error"/> with the updated <see cref="Source"/>.</returns>
    public Error WithSource(string source) =>
        this with { Source = source };

    /// <summary>Returns a copy of this error with the specified <paramref name="target"/>.</summary>
    /// <param name="target">The target field or entity to attach.</param>
    /// <returns>A new <see cref="Error"/> with the updated <see cref="Target"/>.</returns>
    public Error WithTarget(string target) =>
        this with { Target = target };
}
