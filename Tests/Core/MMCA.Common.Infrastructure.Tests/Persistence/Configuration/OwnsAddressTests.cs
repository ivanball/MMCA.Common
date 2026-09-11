using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MMCA.Common.Infrastructure.Persistence.Configuration;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Configuration;

/// <summary>
/// Tests for <c>EntityTypeBuilderExtensions.OwnsAddress</c>. The load-bearing assertion is
/// <b>zero schema drift</b>: a consumer that swaps its hand-written owned-Address block for this
/// helper must produce a byte-identical model, so the swap needs no migration. The two line
/// properties are already named <c>AddressLine1</c>/<c>AddressLine2</c>, so the default
/// <c>"Address"</c> prefix must not double up into <c>AddressAddressLine1</c>. The
/// control entity below is a verbatim copy of MMCA.Store's Identity <c>CustomerConfiguration</c>
/// block, and every relational facet of the helper is compared against it.
/// </summary>
public sealed class OwnsAddressTests : IDisposable
{
    private readonly AddressTestDbContext _dbContext = AddressTestDbContext.Create();

    public void Dispose() => _dbContext.Dispose();

    [Theory]
    [InlineData(nameof(Address.AddressLine1), "AddressLine1")]
    [InlineData(nameof(Address.AddressLine2), "AddressLine2")]
    [InlineData(nameof(Address.City), "AddressCity")]
    [InlineData(nameof(Address.State), "AddressState")]
    [InlineData(nameof(Address.ZipCode), "AddressZipCode")]
    [InlineData(nameof(Address.Country), "AddressCountry")]
    public void OwnsAddress_ProducesTheExistingColumnNames(string propertyName, string columnName) =>
        OwnedType<HelperOwner>(nameof(HelperOwner.Address))
            .FindProperty(propertyName)!
            .GetColumnName()
            .Should().Be(columnName);

    [Theory]
    [InlineData(nameof(Address.AddressLine1))]
    [InlineData(nameof(Address.AddressLine2))]
    [InlineData(nameof(Address.City))]
    [InlineData(nameof(Address.State))]
    [InlineData(nameof(Address.ZipCode))]
    [InlineData(nameof(Address.Country))]
    public void OwnsAddress_MatchesTheHandRolledBlock_FacetForFacet(string propertyName) =>
        Facets(OwnedType<HelperOwner>(nameof(HelperOwner.Address)), propertyName)
            .Should().Be(Facets(OwnedType<HandRolledOwner>(nameof(HandRolledOwner.Address)), propertyName));

    [Fact]
    public void OwnsAddress_MatchesTheHandRolledBlock_OnNavigationRequiredness() =>
        Navigation<HelperOwner>(nameof(HelperOwner.Address)).ForeignKey.IsRequiredDependent
            .Should().Be(Navigation<HandRolledOwner>(nameof(HandRolledOwner.Address)).ForeignKey.IsRequiredDependent);

    [Fact]
    public void OwnsAddress_MapsOnlyTheFirstLineAsRequired()
    {
        var owned = OwnedType<HelperOwner>(nameof(HelperOwner.Address));

        owned.FindProperty(nameof(Address.AddressLine1))!.IsNullable.Should().BeFalse(
            "AddressLine1 is the one field AddressInvariants requires");
        owned.FindProperty(nameof(Address.City))!.IsNullable.Should().BeTrue(
            "the remaining five fields are optional so international address formats fit");
    }

    [Fact]
    public void OwnsAddress_TakesItsLengthsFromTheDomainInvariants()
    {
        var owned = OwnedType<HelperOwner>(nameof(HelperOwner.Address));

        owned.FindProperty(nameof(Address.AddressLine1))!.GetMaxLength().Should().Be(AddressInvariants.AddressLine1MaxLength);
        owned.FindProperty(nameof(Address.City))!.GetMaxLength().Should().Be(AddressInvariants.CityMaxLength);
        owned.FindProperty(nameof(Address.ZipCode))!.GetMaxLength().Should().Be(AddressInvariants.ZipCodeMaxLength);
        owned.FindProperty(nameof(Address.Country))!.GetMaxLength().Should().Be(AddressInvariants.CountryMaxLength);
        owned.FindProperty(nameof(Address.AddressLine1))!.IsUnicode().Should().BeFalse();
    }

    [Theory]
    [InlineData(nameof(Address.AddressLine1), "ShippingAddressLine1")]
    [InlineData(nameof(Address.City), "ShippingAddressCity")]
    public void OwnsAddress_WithAPrefix_NamesASecondAddressOnTheSameOwner(string propertyName, string columnName) =>
        OwnedType<HelperOwner>(nameof(HelperOwner.ShippingAddress))
            .FindProperty(propertyName)!
            .GetColumnName()
            .Should().Be(columnName);

    [Fact]
    public async Task OwnsAddress_RoundTripsAnAddress()
    {
        _dbContext.HelperOwners.Add(new HelperOwner
        {
            Address = Address.Create("1 Peachtree St", null, "Atlanta", "GA", "30303", "USA").Value!,
        });
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        _dbContext.ChangeTracker.Clear();

        var read = await _dbContext.HelperOwners.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        read.Address!.AddressLine1.Should().Be("1 Peachtree St");
        read.Address.AddressLine2.Should().BeNull();
        read.Address.ZipCode.Should().Be("30303");
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

    /// <summary>Owner mapped through the shared <c>OwnsAddress</c> helper.</summary>
    public sealed class HelperOwner
    {
        public int Id { get; set; }

        public Address? Address { get; set; }

        public Address? ShippingAddress { get; set; }
    }

    /// <summary>Control owner mapped with the hand-rolled block the helper replaces.</summary>
    public sealed class HandRolledOwner
    {
        public int Id { get; set; }

        public Address? Address { get; set; }
    }

    public sealed class AddressTestDbContext : DbContext
    {
        private AddressTestDbContext(DbContextOptions<AddressTestDbContext> options)
            : base(options)
        {
        }

        public DbSet<HelperOwner> HelperOwners => Set<HelperOwner>();

        public DbSet<HandRolledOwner> HandRolledOwners => Set<HandRolledOwner>();

        public static AddressTestDbContext Create()
        {
            var options = new DbContextOptionsBuilder<AddressTestDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            var context = new AddressTestDbContext(options);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<HelperOwner>(builder =>
            {
                builder.HasKey(p => p.Id);
                builder.OwnsAddress(p => p.Address);
                builder.OwnsAddress(p => p.ShippingAddress, "ShippingAddress");
            });

            modelBuilder.Entity<HandRolledOwner>(builder =>
            {
                builder.HasKey(p => p.Id);

                // Verbatim copy of MMCA.Store Identity CustomerConfiguration: this control is the
                // code the helper replaces, so a facet difference here is a migration in a consumer.
                builder.OwnsOne(p => p.Address, addressBuilder =>
                {
                    addressBuilder.Property(a => a.AddressLine1)
                        .HasColumnName("AddressLine1")
                        .HasMaxLength(AddressInvariants.AddressLine1MaxLength)
                        .IsUnicode(false)
                        .IsRequired();

                    addressBuilder.Property(a => a.AddressLine2)
                        .HasColumnName("AddressLine2")
                        .HasMaxLength(AddressInvariants.AddressLine2MaxLength)
                        .IsUnicode(false);

                    addressBuilder.Property(a => a.City)
                        .HasColumnName("AddressCity")
                        .HasMaxLength(AddressInvariants.CityMaxLength)
                        .IsUnicode(false);

                    addressBuilder.Property(a => a.State)
                        .HasColumnName("AddressState")
                        .HasMaxLength(AddressInvariants.StateMaxLength)
                        .IsUnicode(false);

                    addressBuilder.Property(a => a.ZipCode)
                        .HasColumnName("AddressZipCode")
                        .HasMaxLength(AddressInvariants.ZipCodeMaxLength)
                        .IsUnicode(false);

                    addressBuilder.Property(a => a.Country)
                        .HasColumnName("AddressCountry")
                        .HasMaxLength(AddressInvariants.CountryMaxLength)
                        .IsUnicode(false);
                });

                builder.Navigation(p => p.Address)
                    .IsRequired(false);
            });
        }
    }
}
