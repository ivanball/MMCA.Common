using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Shared.ValueObjects.Contact;
using MMCA.Common.Shared.ValueObjects.Financial;

namespace MMCA.Common.Infrastructure.Persistence.Configuration;

/// <summary>
/// Extension members for <see cref="EntityTypeBuilder{TEntity}"/> used from entity type
/// configurations.
/// </summary>
public static class EntityTypeBuilderExtensions
{
    /// <summary>
    /// The redundant leading word two <see cref="Address"/> property names already carry, dropped
    /// before a column prefix is applied.
    /// </summary>
    private const string AddressPropertyPrefix = "Address";

    /// <summary>
    /// Read-leg fallback for the Currency value converter: the "no currency" sentinel that
    /// <see cref="Money.Zero()"/> carries, whose Code is the empty string. That sentinel is internal
    /// to MMCA.Common.Shared, so a zero Money is the only public handle on it.
    /// </summary>
    private static readonly Currency NoCurrency = Money.Zero().Currency;

    extension<TOwner>(EntityTypeBuilder<TOwner> builder)
        where TOwner : class
    {
        /// <summary>
        /// Maps a <see cref="Money"/> property as an owned type flattened into two columns on the
        /// owner's table: the decimal amount and the ISO 4217 currency code.
        /// <code>
        /// builder.OwnsMoney(p => p.Total, "TotalAmount", "TotalCurrency", required: false);
        /// </code>
        /// <para>
        /// <b>Round-trip contract:</b> every value the WRITE leg can produce must materialize back
        /// into a non-null Currency, including the empty code a zero <see cref="Money"/> persists
        /// and any code that is no longer in <c>Currency.All</c>. An aggregate can seed a zero total
        /// (<see cref="Money.Zero()"/>, whose Code is the empty string) and leave it there, so the
        /// write leg genuinely persists codes that <see cref="Currency.FromCode"/> rejects. A bare
        /// <c>.Value!</c> turned those rows into a null Currency inside <see cref="Money"/>, which is
        /// a materialization-time <see cref="NullReferenceException"/> waiting for the first read.
        /// Falling back to the sentinel keeps them readable.
        /// </para>
        /// </summary>
        /// <param name="navigationExpression">The <see cref="Money"/> navigation to map.</param>
        /// <param name="amountColumnName">Column name for <see cref="Money.Amount"/>.</param>
        /// <param name="currencyColumnName">Column name for the ISO 4217 code of <see cref="Money.Currency"/>.</param>
        /// <param name="required">
        /// Whether the navigation itself is required. <see langword="true"/> (the default) matches a
        /// price that must always be present; pass <see langword="false"/> for a total the owner can
        /// leave unset. This is the only facet that differs across the existing call sites, so it is
        /// the only one parameterized beyond the two column names.
        /// </param>
        /// <returns>The same builder instance for chaining.</returns>
        public EntityTypeBuilder<TOwner> OwnsMoney(
            Expression<Func<TOwner, Money?>> navigationExpression,
            string amountColumnName,
            string currencyColumnName,
            bool required = true)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(navigationExpression);
            ArgumentException.ThrowIfNullOrWhiteSpace(amountColumnName);
            ArgumentException.ThrowIfNullOrWhiteSpace(currencyColumnName);

            builder.OwnsOne(navigationExpression, moneyBuilder =>
            {
                moneyBuilder.Property(m => m.Amount)
                    .HasColumnName(amountColumnName)
                    .IsRequired();

                moneyBuilder.Property(m => m.Currency)
                    .HasConversion(
                        currency => currency.Code,
                        code => Currency.FromCode(code).Value ?? NoCurrency)
                    .HasMaxLength(3)
                    .IsUnicode(false)
                    .HasColumnName(currencyColumnName)
                    .IsRequired();
            });

            builder.Navigation(navigationExpression).IsRequired(required);

            return builder;
        }

        /// <summary>
        /// Maps an <see cref="Address"/> property as an owned type flattened into six columns on the
        /// owner's table, using the column names, lengths and required-ness the framework's own
        /// consumers already configure by hand.
        /// <code>
        /// builder.OwnsAddress(p => p.Address);                              // AddressLine1, AddressLine2, AddressCity, ...
        /// builder.OwnsAddress(p => p.ShippingAddress, "ShippingAddress");   // ShippingAddressLine1, ShippingAddressCity, ...
        /// </code>
        /// <para>
        /// <b>Column naming.</b> Each column is the prefix followed by the property name, with the
        /// redundant leading word dropped from the two properties that already carry it: with the
        /// default <c>"Address"</c> prefix that yields <c>AddressLine1</c> and <c>AddressLine2</c>
        /// rather than <c>AddressAddressLine1</c>, and <c>AddressCity</c>, <c>AddressState</c>,
        /// <c>AddressZipCode</c>, <c>AddressCountry</c> for the rest. That is exactly the mapping
        /// the existing hand-written configurations declare, so adopting this helper is a zero-diff
        /// model change: no migration, no snapshot churn.
        /// </para>
        /// <para>
        /// <b>Fixed facets.</b> Max lengths come from <see cref="AddressInvariants"/> (the same
        /// constants the value object validates against, so a column can never be shorter than what
        /// the domain accepts) and every column is non-Unicode <c>varchar</c>. Only
        /// <see cref="Address.AddressLine1"/> is required, matching the value object, whose other
        /// five fields are optional to support international address formats.
        /// </para>
        /// </summary>
        /// <param name="navigationExpression">The <see cref="Address"/> navigation to map.</param>
        /// <param name="columnPrefix">
        /// The prefix for the six column names. Defaults to <c>"Address"</c>, which reproduces the
        /// existing mapping; pass a different prefix for a second address on the same owner.
        /// </param>
        /// <param name="required">
        /// Whether the navigation itself is required. <see langword="false"/> (the default) matches
        /// the existing call sites, where an owner may have no address at all.
        /// </param>
        /// <returns>The same builder instance for chaining.</returns>
        public EntityTypeBuilder<TOwner> OwnsAddress(
            Expression<Func<TOwner, Address?>> navigationExpression,
            string columnPrefix = "Address",
            bool required = false)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(navigationExpression);
            ArgumentException.ThrowIfNullOrWhiteSpace(columnPrefix);

            builder.OwnsOne(navigationExpression, addressBuilder =>
            {
                addressBuilder.Property(a => a.AddressLine1)
                    .HasColumnName(AddressColumn(columnPrefix, nameof(Address.AddressLine1)))
                    .HasMaxLength(AddressInvariants.AddressLine1MaxLength)
                    .IsUnicode(false)
                    .IsRequired();

                addressBuilder.Property(a => a.AddressLine2)
                    .HasColumnName(AddressColumn(columnPrefix, nameof(Address.AddressLine2)))
                    .HasMaxLength(AddressInvariants.AddressLine2MaxLength)
                    .IsUnicode(false);

                addressBuilder.Property(a => a.City)
                    .HasColumnName(AddressColumn(columnPrefix, nameof(Address.City)))
                    .HasMaxLength(AddressInvariants.CityMaxLength)
                    .IsUnicode(false);

                addressBuilder.Property(a => a.State)
                    .HasColumnName(AddressColumn(columnPrefix, nameof(Address.State)))
                    .HasMaxLength(AddressInvariants.StateMaxLength)
                    .IsUnicode(false);

                addressBuilder.Property(a => a.ZipCode)
                    .HasColumnName(AddressColumn(columnPrefix, nameof(Address.ZipCode)))
                    .HasMaxLength(AddressInvariants.ZipCodeMaxLength)
                    .IsUnicode(false);

                addressBuilder.Property(a => a.Country)
                    .HasColumnName(AddressColumn(columnPrefix, nameof(Address.Country)))
                    .HasMaxLength(AddressInvariants.CountryMaxLength)
                    .IsUnicode(false);
            });

            builder.Navigation(navigationExpression).IsRequired(required);

            return builder;
        }
    }

    /// <summary>
    /// Joins a column prefix to an address property name without doubling the word "Address": the
    /// two line properties are already named <c>AddressLine1</c>/<c>AddressLine2</c>, so the leading
    /// word is dropped from the property name before the prefix is applied. The default
    /// <c>"Address"</c> prefix therefore yields <c>AddressLine1</c> (not
    /// <c>AddressAddressLine1</c>) and <c>AddressCity</c>, and a <c>"ShippingAddress"</c> prefix
    /// yields <c>ShippingAddressLine1</c> and <c>ShippingAddressCity</c>.
    /// </summary>
    /// <param name="columnPrefix">The configured prefix.</param>
    /// <param name="propertyName">The <see cref="Address"/> property name.</param>
    /// <returns>The column name.</returns>
    private static string AddressColumn(string columnPrefix, string propertyName)
        => columnPrefix + (propertyName.StartsWith(AddressPropertyPrefix, StringComparison.Ordinal)
            ? propertyName[AddressPropertyPrefix.Length..]
            : propertyName);
}
