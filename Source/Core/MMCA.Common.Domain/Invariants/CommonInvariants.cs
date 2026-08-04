using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Globalization;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Domain.Invariants;

/// <summary>
/// Reusable domain invariant checks shared across modules.
/// Module-specific invariant classes should delegate to these helpers
/// for common validation patterns (string-not-empty, ID-not-default, etc.).
/// </summary>
public static class CommonInvariants
{
    /// <summary>The light theme value accepted by <see cref="EnsurePreferredThemeIsValid"/>.</summary>
    public const string LightTheme = "light";

    /// <summary>The dark theme value accepted by <see cref="EnsurePreferredThemeIsValid"/>.</summary>
    public const string DarkTheme = "dark";

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

    /// <summary>
    /// Validates that an integer value is greater than zero.
    /// </summary>
    /// <param name="value">The integer value to validate.</param>
    /// <param name="code">The error code (e.g., "Order.Line.Quantity.NotPositive").</param>
    /// <param name="message">The error message (e.g., "Quantity must be positive.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureIntIsPositive(
        int value, string code, string message, string source, string target)
        => value <= 0
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Validates that a monetary amount is not negative. Zero passes (e.g., free items);
    /// a <see langword="null"/> value fails, matching <see cref="EnsureBytesAreNotEmpty"/>.
    /// </summary>
    /// <param name="value">The monetary amount to validate.</param>
    /// <param name="code">The error code (e.g., "Order.Line.Price.Negative").</param>
    /// <param name="message">The error message (e.g., "Price cannot be negative.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureMoneyIsNotNegative(
        Money value, string code, string message, string source, string target)
        => value is null || value.IsNegative
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Validates that a collection is not null and contains at least one element.
    /// </summary>
    /// <typeparam name="T">The collection element type.</typeparam>
    /// <param name="value">The collection to validate.</param>
    /// <param name="code">The error code (e.g., "Order.Lines.Empty").</param>
    /// <param name="message">The error message (e.g., "Order must not be empty.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureCollectionIsNotEmpty<T>(
        IReadOnlyCollection<T> value, string code, string message, string source, string target)
        => value is null || value.Count == 0
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Validates that a preferred culture is <see langword="null"/> (follow the request default)
    /// or one of the framework's supported cultures (ADR-027).
    /// </summary>
    /// <param name="culture">The culture name to validate (e.g., "es").</param>
    /// <param name="code">The error code (e.g., "User.PreferredCulture.Invalid").</param>
    /// <param name="message">The error message (e.g., "Culture 'xx' is not supported.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsurePreferredCultureIsValid(
        string? culture, string code, string message, string source, string target)
        => culture is null || SupportedCultures.IsSupported(culture)
            ? Result.Success()
            : Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target));

    /// <summary>
    /// Validates that a preferred theme is <see langword="null"/> (follow the system preference),
    /// <see cref="LightTheme"/>, or <see cref="DarkTheme"/>, matched case-insensitively (ADR-028).
    /// </summary>
    /// <param name="theme">The theme name to validate.</param>
    /// <param name="code">The error code (e.g., "User.PreferredTheme.Invalid").</param>
    /// <param name="message">The error message (e.g., "Theme 'neon' is not valid.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsurePreferredThemeIsValid(
        string? theme, string code, string message, string source, string target)
        => theme is null
           || string.Equals(theme, LightTheme, StringComparison.OrdinalIgnoreCase)
           || string.Equals(theme, DarkTheme, StringComparison.OrdinalIgnoreCase)
            ? Result.Success()
            : Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target));
}
