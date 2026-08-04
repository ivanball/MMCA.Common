using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Infrastructure.Persistence.Conversions;

/// <summary>
/// EF Core value converter that stores a <see cref="PhoneNumber"/> as its trimmed string value and
/// rebuilds the value object on read. Mapping through <c>HasConversion</c> rather than
/// <c>OwnsOne</c> keeps the backing column a plain string column, so adopting the value object on a
/// property that used to be a <see cref="string"/> is not a schema change.
/// <para>
/// <b>Usage:</b> apply to a non-nullable <see cref="PhoneNumber"/> property in an entity configuration:
/// <code>
/// builder.Property(p => p.PhoneNumber)
///     .HasConversion(new PhoneNumberValueConverter())
///     .HasMaxLength(PhoneNumberInvariants.MaxLength)
///     .IsUnicode(false)
///     .IsRequired();
/// </code>
/// Column facets (max length, unicode, requiredness) deliberately stay at the call site: they
/// differ per entity and are not the converter's business. Use
/// <see cref="NullablePhoneNumberValueConverter"/> for an optional <c>PhoneNumber?</c> property.
/// </para>
/// <para>
/// <b>Read-leg contract:</b> the read leg trusts the column. <see cref="PhoneNumber.Create"/>
/// returns a failed <c>Result</c> for a value that does not validate, and the null-forgiving
/// <c>.Value!</c> then materializes a <see langword="null"/> reference for that row. Every value the
/// write leg can produce round-trips, because the write leg can only persist an already-validated
/// <see cref="PhoneNumber"/>; only a value written outside EF (a manual script, a data fix) can
/// break the contract.
/// </para>
/// </summary>
public sealed class PhoneNumberValueConverter : ValueConverter<PhoneNumber, string>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PhoneNumberValueConverter"/> class.
    /// </summary>
    public PhoneNumberValueConverter()
        : base(
            phoneNumber => phoneNumber.Value,
            value => PhoneNumber.Create(value).Value!)
    {
    }
}

/// <summary>
/// Nullable counterpart of <see cref="PhoneNumberValueConverter"/> for an optional
/// <c>PhoneNumber?</c> property. Both legs pass <see langword="null"/> straight through, so "no
/// phone number" stays a NULL column value rather than becoming an empty string or a failed
/// <see cref="PhoneNumber.Create"/> call.
/// <para>
/// <b>Usage:</b>
/// <code>
/// builder.Property(p => p.PhoneNumber)
///     .HasConversion(new NullablePhoneNumberValueConverter())
///     .HasMaxLength(PhoneNumberInvariants.MaxLength)
///     .IsRequired(false);
/// </code>
/// </para>
/// </summary>
public sealed class NullablePhoneNumberValueConverter : ValueConverter<PhoneNumber?, string?>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NullablePhoneNumberValueConverter"/> class.
    /// </summary>
    public NullablePhoneNumberValueConverter()
        : base(
            phoneNumber => phoneNumber == null ? null : phoneNumber.Value,
            value => value == null ? null : PhoneNumber.Create(value).Value)
    {
    }
}
