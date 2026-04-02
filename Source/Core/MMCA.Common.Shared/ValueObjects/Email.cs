using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.ValueObjects;

/// <summary>
/// Immutable value object representing a validated email address. The value is normalized
/// to lowercase at creation time. Configured in EF via <c>HasConversion</c> (not <c>OwnsOne</c>)
/// so the backing column remains <c>nvarchar</c>.
/// </summary>
[DataContract]
public sealed record Email : ValueObject
{
    /// <summary>Gets the normalized (lowercase) email address string.</summary>
    [DataMember(Order = 1)]
    public string Value { get; }

    [JsonConstructor]
    private Email(string value) => Value = value;

    /// <summary>
    /// Creates an <see cref="Email"/> after validating format and normalizing to lowercase.
    /// </summary>
    /// <param name="value">The raw email address string.</param>
    /// <returns>A success result with the email, or a validation error if the format is invalid.</returns>
    public static Result<Email> Create(string value)
    {
        string trimmed = value?.Trim() ?? string.Empty;

        var result = EmailInvariants.EnsureEmailIsValid(trimmed, nameof(Create));
        if (result.IsFailure)
            return Result.Failure<Email>(result.Errors);

#pragma warning disable CA1308 // Email addresses are conventionally lowercase per RFC 5321
        return Result.Success(new Email(trimmed.ToLowerInvariant()));
#pragma warning restore CA1308
    }

    /// <summary>Implicit conversion to <see cref="string"/> for backward compatibility.</summary>
    /// <param name="email">The email value object.</param>
    public static implicit operator string(Email email) => email.Value;

    /// <inheritdoc />
    public override string ToString() => Value;
}
