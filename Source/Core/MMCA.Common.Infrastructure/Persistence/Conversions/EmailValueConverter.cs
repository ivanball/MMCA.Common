using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Infrastructure.Persistence.Conversions;

/// <summary>
/// EF Core value converter that stores an <see cref="Email"/> as its normalized string value and
/// rebuilds the value object on read. Mapping through <c>HasConversion</c> rather than
/// <c>OwnsOne</c> keeps the backing column a plain string column, so adopting the value object on a
/// property that used to be a <see cref="string"/> is not a schema change.
/// <para>
/// <b>Usage:</b> apply to a non-nullable <see cref="Email"/> property in an entity configuration:
/// <code>
/// builder.Property(p => p.Email)
///     .HasConversion(new EmailValueConverter())
///     .HasMaxLength(EmailInvariants.MaxLength)
///     .IsUnicode(false)
///     .IsRequired();
/// </code>
/// Column facets (max length, unicode, requiredness) deliberately stay at the call site: they
/// differ per entity and are not the converter's business. Use
/// <see cref="NullableEmailValueConverter"/> for an optional <c>Email?</c> property.
/// </para>
/// <para>
/// <b>Read-leg contract:</b> the read leg trusts the column. <see cref="Email.Create"/> returns a
/// failed <c>Result</c> for a value that does not validate, and the null-forgiving
/// <c>.Value!</c> then materializes a <see langword="null"/> reference for that row. Every value the
/// write leg can produce round-trips, because the write leg can only persist an already-validated
/// <see cref="Email"/>; only a value written outside EF (a manual script, a data fix) can break the
/// contract.
/// </para>
/// </summary>
public sealed class EmailValueConverter : ValueConverter<Email, string>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EmailValueConverter"/> class.
    /// </summary>
    public EmailValueConverter()
        : base(
            email => email.Value,
            value => Email.Create(value).Value!)
    {
    }
}

/// <summary>
/// Nullable counterpart of <see cref="EmailValueConverter"/> for an optional <c>Email?</c> property.
/// Both legs pass <see langword="null"/> straight through, so "no email" stays a NULL column value
/// rather than becoming an empty string or a failed <see cref="Email.Create"/> call.
/// <para>
/// <b>Usage:</b>
/// <code>
/// builder.Property(p => p.Email)
///     .HasConversion(new NullableEmailValueConverter())
///     .HasMaxLength(SpeakerInvariants.EmailMaxLength)
///     .IsRequired(false);
/// </code>
/// </para>
/// </summary>
public sealed class NullableEmailValueConverter : ValueConverter<Email?, string?>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NullableEmailValueConverter"/> class.
    /// </summary>
    public NullableEmailValueConverter()
        : base(
            email => email == null ? null : email.Value,
            value => value == null ? null : Email.Create(value).Value)
    {
    }
}
