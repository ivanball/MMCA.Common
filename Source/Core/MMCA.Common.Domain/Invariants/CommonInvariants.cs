using System.Diagnostics.CodeAnalysis;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Globalization;
using MMCA.Common.Shared.ValueObjects.Financial;

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

    /// <summary>
    /// Validates that an enum value is one of the members declared on its type, rejecting the
    /// arbitrary integers a cast or a deserialized payload can produce.
    /// </summary>
    /// <typeparam name="TEnum">The enum type.</typeparam>
    /// <param name="value">The enum value to validate.</param>
    /// <param name="code">The error code (e.g., "CheckIn.Scope.Invalid").</param>
    /// <param name="message">The error message (e.g., "Check-in scope is not valid.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureEnumIsDefined<TEnum>(
        TEnum value, string code, string message, string source, string target)
        where TEnum : struct, Enum
        => Enum.IsDefined(value)
            ? Result.Success()
            : Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target));

    /// <summary>
    /// Validates that the end of a range is not before its start. Equal values pass, so a
    /// single-day event or a zero-length window is allowed; use a separate strict check when the
    /// domain forbids a zero-length range.
    /// </summary>
    /// <typeparam name="T">The comparable range endpoint type (e.g. <see cref="DateOnly"/>, <see cref="DateTime"/>).</typeparam>
    /// <param name="start">The start of the range.</param>
    /// <param name="end">The end of the range.</param>
    /// <param name="code">The error code (e.g., "Event.DateRange.Invalid").</param>
    /// <param name="message">The error message (e.g., "Event end date must be on or after the start date.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureEndIsNotBeforeStart<T>(
        T start, T end, string code, string message, string source, string target)
        where T : IComparable<T>
        => end.CompareTo(start) < 0
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Validates that a required string's length falls within an inclusive range. A
    /// <see langword="null"/>, empty, or whitespace-only value fails, so this is the single-error
    /// equivalent of pairing <see cref="EnsureStringIsNotEmpty"/> with
    /// <see cref="EnsureStringMaxLength"/> when the domain reports one bound violation rather
    /// than two.
    /// </summary>
    /// <param name="value">The string value to validate.</param>
    /// <param name="minLength">The minimum allowed length, inclusive.</param>
    /// <param name="maxLength">The maximum allowed length, inclusive.</param>
    /// <param name="code">The error code (e.g., "PointsEntry.SubjectKey.Invalid").</param>
    /// <param name="message">The error message (e.g., "Subject key must be 1-100 characters.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureStringLengthIsWithin(
        string? value, int minLength, int maxLength, string code, string message, string source, string target)
        => string.IsNullOrWhiteSpace(value) || value.Length < minLength || value.Length > maxLength
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Validates that an optional string is absent or within a maximum length. A
    /// <see langword="null"/> or empty value passes.
    /// </summary>
    /// <remarks>
    /// Behaviourally the same bound as <see cref="EnsureStringMaxLength"/>, which is already
    /// null-tolerant. It exists so an optional field states that intent at the call site instead
    /// of guarding with a redundant <c>string.IsNullOrEmpty(value) ? Result.Success() : ...</c>
    /// ternary, which is what the duplicated app-side copies do today.
    /// </remarks>
    /// <param name="value">The optional string value to validate.</param>
    /// <param name="maxLength">The maximum allowed length.</param>
    /// <param name="code">The error code (e.g., "Activity.VenueUrl.TooLong").</param>
    /// <param name="message">The error message.</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureOptionalStringMaxLength(
        string? value, int maxLength, string code, string message, string source, string target)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? Result.Success()
            : Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target));

    /// <summary>
    /// Validates that a time zone identifier is <see langword="null"/> (the field is optional) or
    /// resolves to a time zone this host recognizes.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="TimeZoneInfo.TryFindSystemTimeZoneById(string, out TimeZoneInfo?)"/> rather
    /// than catching <see cref="TimeZoneNotFoundException"/> around
    /// <see cref="TimeZoneInfo.FindSystemTimeZoneById(string)"/>, keeping the check off the
    /// exception path and also rejecting a corrupt time zone entry, which the exception form let
    /// through. An empty or whitespace identifier is not recognized and therefore fails; pair with
    /// <see cref="EnsureStringIsNotEmpty"/> when the domain reports emptiness under its own code.
    /// </remarks>
    /// <param name="timeZone">The time zone identifier to validate (e.g., "America/New_York").</param>
    /// <param name="code">The error code (e.g., "Event.TimeZone.Invalid").</param>
    /// <param name="message">The error message.</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureTimeZoneIsValid(
        string? timeZone, string code, string message, string source, string target)
        => timeZone is null || TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _)
            ? Result.Success()
            : Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target));

    /// <summary>
    /// Validates that a URL is absent (a <see langword="null"/> or empty optional field) or an
    /// absolute <c>http</c>/<c>https</c> URI.
    /// </summary>
    /// <remarks>
    /// Bounded-string validation alone lets <c>javascript:</c> and <c>data:</c> values through,
    /// which reach the browser as executable content once a link or an image renders them.
    /// Requiring an absolute URI on an http scheme closes that without constraining the host or
    /// path. Length stays a separate concern: compose with
    /// <see cref="EnsureOptionalStringMaxLength"/> via <see cref="Result.Combine"/>.
    /// </remarks>
    /// <param name="url">The URL to validate.</param>
    /// <param name="code">The error code (e.g., "Sponsor.LogoUrl.Invalid").</param>
    /// <param name="message">The error message.</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    [SuppressMessage(
        "Design",
        "CA1054:URI-like parameters should not be strings",
        Justification = "The point of the check is to validate an untrusted string BEFORE anything turns it into a Uri; a Uri parameter would have already accepted the javascript:/data: value this rejects.")]
    public static Result EnsureUrlIsWellFormed(
        string? url, string code, string message, string source, string target)
        => string.IsNullOrEmpty(url) || IsAbsoluteHttpUrl(url)
            ? Result.Success()
            : Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target));

    /// <summary>
    /// Validates that a count falls within an inclusive range.
    /// </summary>
    /// <param name="count">The count to validate.</param>
    /// <param name="minCount">The minimum allowed count, inclusive.</param>
    /// <param name="maxCount">The maximum allowed count, inclusive.</param>
    /// <param name="code">The error code (e.g., "LivePoll.Options.CountInvalid").</param>
    /// <param name="message">The error message (e.g., "A poll must have between 2 and 10 options.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureCountIsWithin(
        int count, int minCount, int maxCount, string code, string message, string source, string target)
        => count < minCount || count > maxCount
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Validates that a collection holds no elements, the guard a delete needs when dependants
    /// must be removed first. A <see langword="null"/> collection passes.
    /// </summary>
    /// <typeparam name="T">The collection element type.</typeparam>
    /// <param name="value">The collection to validate.</param>
    /// <param name="code">The error code (e.g., "Category.HasProducts").</param>
    /// <param name="message">The error message (e.g., "Cannot delete a category that has products assigned to it.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureCollectionIsEmpty<T>(
        IReadOnlyCollection<T>? value, string code, string message, string source, string target)
        => value is null || value.Count == 0
            ? Result.Success()
            : Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target));

    /// <summary>
    /// Validates that a sequence contains no duplicates under <paramref name="comparer"/>. A
    /// <see langword="null"/> sequence passes, being vacuously unique.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="values">The values to check for duplicates.</param>
    /// <param name="comparer">
    /// The equality comparer, or <see langword="null"/> for the type's default. Pass
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> for case-insensitive text uniqueness.
    /// </param>
    /// <param name="code">The error code (e.g., "LivePoll.Options.Duplicate").</param>
    /// <param name="message">The error message (e.g., "Option texts must be unique.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureValuesAreUnique<T>(
        IEnumerable<T>? values,
        IEqualityComparer<T>? comparer,
        string code,
        string message,
        string source,
        string target)
    {
        if (values is null)
        {
            return Result.Success();
        }

        var seen = new HashSet<T>(comparer);

        return values.Any(value => !seen.Add(value))
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();
    }

    /// <summary>
    /// Validates that a state flag is set, the guard for an action that requires the flag
    /// (e.g. an event must be published).
    /// </summary>
    /// <param name="value">The flag to validate.</param>
    /// <param name="code">The error code (e.g., "Event.NotPublished").</param>
    /// <param name="message">The error message (e.g., "This action requires the event to be published.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureFlagIsTrue(
        bool value, string code, string message, string source, string target)
        => value
            ? Result.Success()
            : Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target));

    /// <summary>
    /// Validates that a state flag is clear, the guard for an action the flag forbids
    /// (e.g. a service session cannot be edited).
    /// </summary>
    /// <param name="value">The flag to validate.</param>
    /// <param name="code">The error code (e.g., "Session.IsServiceSession").</param>
    /// <param name="message">The error message (e.g., "This action is not available for service sessions.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureFlagIsFalse(
        bool value, string code, string message, string source, string target)
        => value
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Validates that an optional integer is absent or greater than zero. A
    /// <see langword="null"/> value passes; zero and negatives fail.
    /// </summary>
    /// <param name="value">The optional integer to validate.</param>
    /// <param name="code">The error code (e.g., "Room.Capacity.Invalid").</param>
    /// <param name="message">The error message (e.g., "Room capacity must be a positive integer.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureNullableIntIsPositive(
        int? value, string code, string message, string source, string target)
        => value is <= 0
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Validates that an integer is not negative. Zero passes, which is what separates this from
    /// <see cref="EnsureIntIsPositive"/> (an on-hand quantity may legitimately be zero).
    /// </summary>
    /// <param name="value">The integer value to validate.</param>
    /// <param name="code">The error code (e.g., "Inventory.AvailableQuantity.Negative").</param>
    /// <param name="message">The error message (e.g., "Available quantity cannot be negative.").</param>
    /// <param name="source">The calling method name, used for error tracing.</param>
    /// <param name="target">The property name, used for error targeting.</param>
    /// <returns>A <see cref="Result"/> indicating success or an invariant error.</returns>
    public static Result EnsureIntIsNotNegative(
        int value, string code, string message, string source, string target)
        => value < 0
            ? Result.Failure(Error.Invariant(code: code, message: message, source: source, target: target))
            : Result.Success();

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="url"/> parses as an absolute URI whose
    /// scheme is <c>http</c> or <c>https</c>.
    /// </summary>
    private static bool IsAbsoluteHttpUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal));
}
