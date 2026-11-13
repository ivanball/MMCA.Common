using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Infrastructure.Persistence.Conversions;

/// <summary>
/// EF Core value converter that stores an <see cref="Enumeration{TEnumeration}"/> member as its
/// <see cref="Enumeration{TEnumeration}.Value"/> and rebuilds the member on read. Mapping through
/// <c>HasConversion</c> rather than <c>OwnsOne</c> keeps the backing column a plain
/// <see langword="int"/> column, so replacing a CLR enum property with a smart enumeration is not a
/// schema change.
/// <para>
/// <b>Usage:</b> apply to a non-nullable enumeration property in an entity configuration:
/// <code>
/// builder.Property(p => p.Priority)
///     .HasConversion(new EnumerationValueConverter&lt;Priority&gt;())
///     .IsRequired();
/// </code>
/// Column facets (requiredness, default value, precision) deliberately stay at the call site: they
/// differ per entity and are not the converter's business. Use
/// <see cref="NullableEnumerationValueConverter{TEnumeration}"/> for an optional property.
/// </para>
/// <para>
/// <b>Read-leg contract:</b> the read leg trusts the column.
/// <see cref="Enumeration{TEnumeration}.FromValue"/> returns a failed <c>Result</c> for a value no
/// member declares, and the null-forgiving <c>.Value!</c> then materializes a <see langword="null"/>
/// reference for that row. Every value the write leg can produce round-trips, because the write leg
/// can only persist an already-declared member; only a value written outside EF (a manual script, a
/// data fix, a member deleted from the enumeration after rows were written) can break the contract.
/// </para>
/// </summary>
/// <typeparam name="TEnumeration">The concrete enumeration type being mapped.</typeparam>
public sealed class EnumerationValueConverter<TEnumeration> : ValueConverter<TEnumeration, int>
    where TEnumeration : Enumeration<TEnumeration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EnumerationValueConverter{TEnumeration}"/> class.
    /// </summary>
    public EnumerationValueConverter()
        : base(
            member => member.Value,
            value => Enumeration<TEnumeration>.FromValue(value).Value!)
    {
    }
}

/// <summary>
/// EF Core value converter that stores an <see cref="Enumeration{TEnumeration}"/> member as its
/// <see cref="Enumeration{TEnumeration}.Name"/> and rebuilds the member on read. It keeps a column
/// that already holds names (a CLR enum mapped with <c>HasConversion&lt;string&gt;()</c>) a text
/// column with the same stored values, so replacing that enum with a smart enumeration is neither a
/// column type change nor a data rewrite. Use <see cref="EnumerationValueConverter{TEnumeration}"/>
/// for an <see langword="int"/> column.
/// <para>
/// <b>Usage:</b>
/// <code>
/// builder.Property(t => t.Status)
///     .HasConversion(new EnumerationNameValueConverter&lt;TicketStatus&gt;())
///     .HasMaxLength(32)
///     .IsRequired();
/// </code>
/// </para>
/// <para>
/// <b>Read-leg contract:</b> the same as <see cref="EnumerationValueConverter{TEnumeration}"/>'s.
/// <see cref="Enumeration{TEnumeration}.FromName"/> matches case-insensitively and returns a failed
/// <c>Result</c> for a name no member declares, which the null-forgiving <c>.Value!</c> materializes
/// as a <see langword="null"/> reference for that row.
/// </para>
/// </summary>
/// <typeparam name="TEnumeration">The concrete enumeration type being mapped.</typeparam>
public sealed class EnumerationNameValueConverter<TEnumeration> : ValueConverter<TEnumeration, string>
    where TEnumeration : Enumeration<TEnumeration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EnumerationNameValueConverter{TEnumeration}"/> class.
    /// </summary>
    public EnumerationNameValueConverter()
        : base(
            member => member.Name,
            text => Enumeration<TEnumeration>.FromName(text).Value!)
    {
    }
}

/// <summary>
/// Nullable counterpart of <see cref="EnumerationValueConverter{TEnumeration}"/> for an optional
/// enumeration property. Both legs pass <see langword="null"/> straight through, so "no member
/// selected" stays a NULL column value rather than collapsing onto whichever member happens to
/// declare the value zero.
/// <para>
/// <b>Usage:</b>
/// <code>
/// builder.Property(p => p.Priority)
///     .HasConversion(new NullableEnumerationValueConverter&lt;Priority&gt;())
///     .IsRequired(false);
/// </code>
/// </para>
/// </summary>
/// <typeparam name="TEnumeration">The concrete enumeration type being mapped.</typeparam>
public sealed class NullableEnumerationValueConverter<TEnumeration> : ValueConverter<TEnumeration?, int?>
    where TEnumeration : Enumeration<TEnumeration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NullableEnumerationValueConverter{TEnumeration}"/> class.
    /// </summary>
    public NullableEnumerationValueConverter()
        : base(
            member => member == null ? null : member.Value,
            value => value == null ? null : Enumeration<TEnumeration>.FromValue(value.Value).Value)
    {
    }
}
