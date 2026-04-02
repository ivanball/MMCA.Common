using System.Text.RegularExpressions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.ValueObjects;

/// <summary>
/// Invariant checks and constraints for <see cref="Email"/> value objects.
/// Max-length constant is shared with EF entity configurations and FluentValidation validators.
/// </summary>
public static partial class EmailInvariants
{
    /// <summary>Maximum length for an email address.</summary>
    public static readonly int MaxLength = 256;

    /// <summary>
    /// Validates that the given string is a non-empty, well-formed email address.
    /// Uses a basic pattern check — not full RFC 5322 but covers practical addresses.
    /// </summary>
    /// <param name="email">The email string to validate.</param>
    /// <param name="source">The calling method name, attached to any error for diagnostics.</param>
    /// <returns>A success result if valid, or a failure with an invariant error.</returns>
    public static Result EnsureEmailIsValid(string email, string source)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Result.Failure(Error.Invariant(
                code: "Email.Empty",
                message: "Email address cannot be empty.",
                source: source,
                target: nameof(email)));
        }

        if (email.Length > MaxLength)
        {
            return Result.Failure(Error.Invariant(
                code: "Email.TooLong",
                message: $"Email address cannot exceed {MaxLength} characters.",
                source: source,
                target: nameof(email)));
        }

        if (!EmailRegex.IsMatch(email))
        {
            return Result.Failure(Error.Invariant(
                code: "Email.InvalidFormat",
                message: "Email address format is invalid.",
                source: source,
                target: nameof(email)));
        }

        return Result.Success();
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EmailRegex { get; }
}
