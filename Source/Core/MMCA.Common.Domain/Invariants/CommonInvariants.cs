using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Invariants;

/// <summary>
/// Reusable domain invariant checks shared across modules.
/// Module-specific invariant classes should delegate to these helpers
/// for common validation patterns (string-not-empty, ID-not-default, etc.).
/// </summary>
public static class CommonInvariants
{
    /// <summary>
    /// Validates that a string value is not null, empty, or whitespace.
    /// </summary>
    /// <param name="value">The string value to validate.</param>
    /// <param name="code">The error code (e.g., "Speaker.FirstName.Empty").</param>
    /// <param name="message">The error message (e.g., "Speaker first name cannot be empty.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureStringIsNotEmpty(
        string value, string code, string message, string source, string target)
        => string.IsNullOrWhiteSpace(value)
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Validates that a string value does not exceed the specified maximum length.
    /// Null and empty strings pass (use <see cref="EnsureStringIsNotEmpty"/> for non-empty enforcement).
    /// </summary>
    /// <param name="value">The string value to validate.</param>
    /// <param name="maxLength">The maximum allowed length.</param>
    /// <param name="code">The error code (e.g., "Product.Name.TooLong").</param>
    /// <param name="message">The error message.</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureStringMaxLength(
        string? value, int maxLength, string code, string message, string source, string target)
        => value is not null && value.Length > maxLength
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Validates that a comparable identifier is not equal to its default value.
    /// </summary>
    /// <typeparam name="TId">The identifier type.</typeparam>
    /// <param name="id">The identifier value to validate.</param>
    /// <param name="code">The error code (e.g., "UserSessionBookmark.UserId.Invalid").</param>
    /// <param name="message">The error message (e.g., "User ID must be provided.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureIdIsNotDefault<TId>(
        TId id, string code, string message, string source, string target)
        where TId : struct, IEquatable<TId>
        => id.Equals(default)
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Validates that a byte array is not null or empty.
    /// </summary>
    /// <param name="value">The byte array to validate.</param>
    /// <param name="code">The error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureBytesAreNotEmpty(
        byte[] value, string code, string message, string source, string target)
        => value is null || value.Length == 0
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();
}
