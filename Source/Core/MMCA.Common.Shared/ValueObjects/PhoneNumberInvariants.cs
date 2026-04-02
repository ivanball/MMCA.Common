using System.Text.RegularExpressions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.ValueObjects;

/// <summary>
/// Invariant checks and constraints for <see cref="PhoneNumber"/> value objects.
/// Max-length constant is shared with EF entity configurations and FluentValidation validators.
/// </summary>
public static partial class PhoneNumberInvariants
{
    /// <summary>Maximum length for a phone number.</summary>
    public static readonly int MaxLength = 20;

    /// <summary>Minimum length for a phone number.</summary>
    public static readonly int MinLength = 7;

    /// <summary>
    /// Validates that the given string is a non-empty phone number with allowed characters
    /// (digits, spaces, hyphens, parentheses, plus sign).
    /// </summary>
    /// <param name="phoneNumber">The phone number string to validate.</param>
    /// <param name="source">The calling method name, attached to any error for diagnostics.</param>
    /// <returns>A success result if valid, or a failure with an invariant error.</returns>
    public static Result EnsurePhoneNumberIsValid(string phoneNumber, string source)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return Result.Failure(Error.Invariant(
                code: "PhoneNumber.Empty",
                message: "Phone number cannot be empty.",
                source: source,
                target: nameof(phoneNumber)));
        }

        string trimmed = phoneNumber.Trim();

        if (trimmed.Length < MinLength || trimmed.Length > MaxLength)
        {
            return Result.Failure(Error.Invariant(
                code: "PhoneNumber.InvalidLength",
                message: $"Phone number must be between {MinLength} and {MaxLength} characters.",
                source: source,
                target: nameof(phoneNumber)));
        }

        if (!PhoneNumberRegex.IsMatch(trimmed))
        {
            return Result.Failure(Error.Invariant(
                code: "PhoneNumber.InvalidFormat",
                message: "Phone number contains invalid characters.",
                source: source,
                target: nameof(phoneNumber)));
        }

        return Result.Success();
    }

    [GeneratedRegex(@"^[\d\s\-\(\)\+]+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PhoneNumberRegex { get; }
}
