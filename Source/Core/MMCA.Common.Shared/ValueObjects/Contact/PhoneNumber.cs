using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.ValueObjects.Contact;

/// <summary>
/// Immutable value object representing a validated phone number. Whitespace is trimmed
/// at creation time. Configured in EF via <c>HasConversion</c> (not <c>OwnsOne</c>)
/// so the backing column remains <c>nvarchar</c>: pass the shipped
/// <c>MMCA.Common.Infrastructure.Persistence.Conversions.PhoneNumberValueConverter</c>, or
/// <c>NullablePhoneNumberValueConverter</c> for an optional <c>PhoneNumber?</c> property, rather
/// than hand-rolling the lambda pair per entity configuration.
/// </summary>
[DataContract]
public sealed record PhoneNumber : ValueObject
{
    /// <summary>Gets the trimmed phone number string.</summary>
    [DataMember(Order = 1)]
    public string Value { get; }

    [JsonConstructor]
    private PhoneNumber(string value) => Value = value;

    /// <summary>
    /// Creates a <see cref="PhoneNumber"/> after validating format.
    /// </summary>
    /// <param name="value">The raw phone number string.</param>
    /// <returns>A success result with the phone number, or a validation error if the format is invalid.</returns>
    public static Result<PhoneNumber> Create(string value)
    {
        var result = PhoneNumberInvariants.EnsurePhoneNumberIsValid(value, nameof(Create));
        if (result.IsFailure)
            return Result.Failure<PhoneNumber>(result.Errors);

        return Result.Success(new PhoneNumber(value.Trim()));
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
