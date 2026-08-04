using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Infrastructure.Persistence.Configuration;

/// <summary>
/// Extension members for <see cref="EntityTypeBuilder{TEntity}"/> used from entity type
/// configurations.
/// </summary>
public static class EntityTypeBuilderExtensions
{
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
    }
}
