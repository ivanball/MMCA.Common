using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMCA.Common.Infrastructure.Persistence.Configuration;
using MMCA.Common.Shared.ValueObjects.Financial;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Configuration;

/// <summary>
/// Tests for <c>EntityTypeBuilderExtensions.OwnsMoney</c>. The helper replaces a block that was
/// triplicated across Store's Order, OrderLine and ProductVariant configurations, so the load-bearing
/// assertion is <b>zero schema drift</b>: every relational facet it produces must equal what the
/// hand-rolled block produces, compared against a control entity configured with that block verbatim.
/// The currency round-trip fallback is exercised too: a zero <see cref="Money"/> persists an empty
/// currency code, which <see cref="Currency.FromCode"/> rejects, and without the sentinel fallback it
/// would materialize a null Currency inside <see cref="Money"/>.
/// </summary>
public sealed class OwnsMoneyTests : IDisposable
{
    private readonly MoneyTestDbContext _dbContext = MoneyTestDbContext.Create();

    public void Dispose() => _dbContext.Dispose();

    [Theory]
    [InlineData(nameof(HelperOwner.Total), "TotalAmount", "TotalCurrency")]
    [InlineData(nameof(HelperOwner.UnitPrice), "UnitPriceAmount", "UnitPriceCurrency")]
    [InlineData(nameof(HelperOwner.Price), "PriceAmount", "PriceCurrency")]
    public void OwnsMoney_ProducesTheColumnNamesItWasGiven(
        string navigationName,
        string amountColumnName,
        string currencyColumnName)
    {
        var owned = OwnedType<HelperOwner>(navigationName);

        owned.FindProperty(nameof(Money.Amount))!.GetColumnName().Should().Be(amountColumnName);
        owned.FindProperty(nameof(Money.Currency))!.GetColumnName().Should().Be(currencyColumnName);
    }

    [Theory]
    [InlineData(nameof(HelperOwner.Total))]
    [InlineData(nameof(HelperOwner.UnitPrice))]
    [InlineData(nameof(HelperOwner.Price))]
    public void OwnsMoney_MatchesTheHandRolledBlock_FacetForFacet(string navigationName)
    {
        var helper = OwnedType<HelperOwner>(navigationName);
        var handRolled = OwnedType<HandRolledOwner>(navigationName);

        Facets(helper, nameof(Money.Amount)).Should().Be(Facets(handRolled, nameof(Money.Amount)));
        Facets(helper, nameof(Money.Currency)).Should().Be(Facets(handRolled, nameof(Money.Currency)));
    }

    [Theory]
    [InlineData(nameof(HelperOwner.Total))]
    [InlineData(nameof(HelperOwner.UnitPrice))]
    [InlineData(nameof(HelperOwner.Price))]
    public void OwnsMoney_MatchesTheHandRolledBlock_OnNavigationRequiredness(string navigationName)
    {
        bool helper = Navigation<HelperOwner>(navigationName).ForeignKey.IsRequiredDependent;
        bool handRolled = Navigation<HandRolledOwner>(navigationName).ForeignKey.IsRequiredDependent;

        helper.Should().Be(handRolled);
    }

    [Fact]
    public void OwnsMoney_RequiredFlag_DrivesTheNavigationRequiredness()
    {
        // Total is the Order case (required: false); UnitPrice and Price are the OrderLine and
        // ProductVariant cases (required: true). That flag is the only facet the three sites differ on.
        Navigation<HelperOwner>(nameof(HelperOwner.Total)).ForeignKey.IsRequiredDependent.Should().BeFalse();
        Navigation<HelperOwner>(nameof(HelperOwner.UnitPrice)).ForeignKey.IsRequiredDependent.Should().BeTrue();
        Navigation<HelperOwner>(nameof(HelperOwner.Price)).ForeignKey.IsRequiredDependent.Should().BeTrue();
    }

    [Fact]
    public void OwnsMoney_MapsTheCurrencyColumnAsAThreeCharacterNonUnicodeCode()
    {
        var owned = OwnedType<HelperOwner>(nameof(HelperOwner.Price));
        var currency = owned.FindProperty(nameof(Money.Currency))!;

        currency.GetMaxLength().Should().Be(3);
        currency.IsUnicode().Should().BeFalse();
        currency.GetValueConverter()!.ProviderClrType.Should().Be<string>();
    }

    [Fact]
    public async Task OwnsMoney_RoundTripsARealCurrency()
    {
        // Distinct instances per navigation: EF tracks owned entities by reference, so sharing one
        // Money object across three owned navigations is an identity conflict, not a mapping problem.
        _dbContext.HelperOwners.Add(new HelperOwner
        {
            Price = Money.Create(19.99m, Currency.Usd).Value!,
            UnitPrice = Money.Create(19.99m, Currency.Usd).Value!,
            Total = Money.Create(19.99m, Currency.Usd).Value!,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var read = await _dbContext.HelperOwners.AsNoTracking().SingleAsync();

        read.Price.Amount.Should().Be(19.99m);
        read.Price.Currency.Code.Should().Be("USD");
    }

    [Fact]
    public async Task OwnsMoney_ReadsBackAZeroMoney_WithoutANullCurrency()
    {
        // Money.Zero() carries the "no currency" sentinel, whose Code is the empty string: a code
        // Currency.FromCode rejects. A bare .Value! would materialize a null Currency here, which is
        // a NullReferenceException at materialization time on the very next read.
        _dbContext.HelperOwners.Add(new HelperOwner { Total = Money.Zero() });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var read = await _dbContext.HelperOwners.AsNoTracking().SingleAsync();

        read.Total.Should().NotBeNull();
        read.Total.Currency.Should().NotBeNull();
        read.Total.Currency.Code.Should().BeEmpty();
        read.Total.Currency.Should().BeSameAs(Money.Zero().Currency, "the read leg falls back to the sentinel");
    }

    private static PropertyFacets Facets(IReadOnlyEntityType owned, string propertyName)
    {
        var property = owned.FindProperty(propertyName)!;

        return new PropertyFacets(
            property.GetColumnName(),
            property.GetColumnType(),
            property.IsNullable,
            property.GetMaxLength(),
            property.IsUnicode(),
            property.GetValueConverter()?.ProviderClrType);
    }

    private IReadOnlyEntityType OwnedType<TOwner>(string navigationName)
        where TOwner : class
        => Navigation<TOwner>(navigationName).TargetEntityType;

    private IReadOnlyNavigation Navigation<TOwner>(string navigationName)
        where TOwner : class
        => _dbContext.Model.FindEntityType(typeof(TOwner))!.FindNavigation(navigationName)!;

    private sealed record PropertyFacets(
        string? ColumnName,
        string? ColumnType,
        bool IsNullable,
        int? MaxLength,
        bool? IsUnicode,
        Type? ProviderClrType);

    // ── Test doubles ──

    /// <summary>Owner mapped through the shared <c>OwnsMoney</c> helper.</summary>
    public sealed class HelperOwner
    {
        public int Id { get; set; }

        public Money Total { get; set; } = Money.Zero();

        public Money UnitPrice { get; set; } = Money.Zero();

        public Money Price { get; set; } = Money.Zero();
    }

    /// <summary>Control owner mapped with the hand-rolled block the helper replaces.</summary>
    public sealed class HandRolledOwner
    {
        public int Id { get; set; }

        public Money Total { get; set; } = Money.Zero();

        public Money UnitPrice { get; set; } = Money.Zero();

        public Money Price { get; set; } = Money.Zero();
    }

    public sealed class MoneyTestDbContext : DbContext
    {
        /// <summary>
        /// Read-leg fallback for the Currency value converter, copied verbatim from the consumer
        /// configurations so the control entity below really is the code being replaced.
        /// </summary>
        private static readonly Currency NoCurrency = Money.Zero().Currency;

        private MoneyTestDbContext(DbContextOptions<MoneyTestDbContext> options)
            : base(options)
        {
        }

        public DbSet<HelperOwner> HelperOwners => Set<HelperOwner>();

        public DbSet<HandRolledOwner> HandRolledOwners => Set<HandRolledOwner>();

        public static MoneyTestDbContext Create()
        {
            var options = new DbContextOptionsBuilder<MoneyTestDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new MoneyTestDbContext(options);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<HelperOwner>(builder =>
            {
                builder.HasKey(p => p.Id);
                builder.OwnsMoney(p => p.Total, "TotalAmount", "TotalCurrency", required: false);
                builder.OwnsMoney(p => p.UnitPrice, "UnitPriceAmount", "UnitPriceCurrency");
                builder.OwnsMoney(p => p.Price, "PriceAmount", "PriceCurrency");
            });

            modelBuilder.Entity<HandRolledOwner>(builder =>
            {
                builder.HasKey(p => p.Id);

                // Verbatim copy of MMCA.Store Sales OrderConfiguration (Total, optional navigation).
                builder.OwnsOne(p => p.Total, moneyBuilder =>
                {
                    moneyBuilder.Property(m => m.Amount)
                        .HasColumnName("TotalAmount")
                        .IsRequired();

                    moneyBuilder.Property(m => m.Currency)
                        .HasConversion(
                            currency => currency.Code,
                            code => Currency.FromCode(code).Value ?? NoCurrency)
                        .HasMaxLength(3)
                        .IsUnicode(false)
                        .HasColumnName("TotalCurrency")
                        .IsRequired();
                });

                builder.Navigation(p => p.Total)
                    .IsRequired(false);

                // Verbatim copy of MMCA.Store Sales OrderLineConfiguration (UnitPrice, required).
                builder.OwnsOne(p => p.UnitPrice, moneyBuilder =>
                {
                    moneyBuilder.Property(m => m.Amount)
                        .HasColumnName("UnitPriceAmount")
                        .IsRequired();

                    moneyBuilder.Property(m => m.Currency)
                        .HasConversion(
                            currency => currency.Code,
                            code => Currency.FromCode(code).Value ?? NoCurrency)
                        .HasMaxLength(3)
                        .IsUnicode(false)
                        .HasColumnName("UnitPriceCurrency")
                        .IsRequired();
                });

                builder.Navigation(p => p.UnitPrice)
                    .IsRequired();

                // Verbatim copy of MMCA.Store Catalog ProductVariantConfiguration (Price, required).
                builder.OwnsOne(p => p.Price, moneyBuilder =>
                {
                    moneyBuilder.Property(m => m.Amount)
                        .HasColumnName("PriceAmount")
                        .IsRequired();

                    moneyBuilder.Property(m => m.Currency)
                        .HasConversion(
                            currency => currency.Code,
                            code => Currency.FromCode(code).Value ?? NoCurrency)
                        .HasMaxLength(3)
                        .IsUnicode(false)
                        .HasColumnName("PriceCurrency")
                        .IsRequired();
                });

                builder.Navigation(p => p.Price)
                    .IsRequired();
            });
        }
    }
}
