using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Domain.Privacy;
using MMCA.Common.Infrastructure.Persistence.AuditTrail;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.AuditTrail;

/// <summary>
/// The audit trail never records personal data held in an owned value object whose navigation the
/// owner marks <see cref="PiiAttribute"/>, nor the two-factor material of an account implementing
/// <see cref="ITwoFactorUserState"/>. Every assertion reads the trail rows a real SQLite save
/// committed, through the interceptor pipeline production runs.
/// </summary>
public sealed class AuditTrailOwnedAndTwoFactorRedactionTests : IDisposable
{
    private const string HomeStreet = "1 Private Lane";
    private const string HomeCity = "Hometown";
    private const string HomePostal = "11111";

    private static readonly string?[] AllowedHomeValues = [null, PiiRedactor.RedactedToken];

    private readonly FakeTimeProvider _timeProvider = AuditTrailTestHarness.CreateTimeProvider();
    private readonly RedactionTestContext _context;

    public AuditTrailOwnedAndTwoFactorRedactionTests() =>
        _context = RedactionTestContext.Create(_timeProvider);

    public void Dispose() => _context.Dispose();

    // -- C1: an owner whose owned navigation is [Pii] --
    [Fact]
    public async Task PiiNavigation_OnInsert_NoRowCarriesAnOwnedValue()
    {
        _context.Owners.Add(new PiiOwnerThing { Name = "Owner", Home = Home() });

        await _context.SaveChangesAsync(userId: 1);

        var rows = await RowsAsync();
        rows.Should().NotBeEmpty();
        AssertNoPlaintext(rows, HomeStreet, HomeCity, HomePostal);
        foreach (var row in rows.Where(r => r.PropertyName?.StartsWith("Home.", StringComparison.Ordinal) == true))
        {
            AllowedHomeValues.Should().Contain(row.OldValue);
            AllowedHomeValues.Should().Contain(row.NewValue);
        }
    }

    [Fact]
    public async Task PiiNavigation_FirstSetOnAnExistingOwner_RedactsEveryOwnedColumn()
    {
        var owner = new PiiOwnerThing { Name = "Owner" };
        _context.Owners.Add(owner);
        await _context.SaveChangesAsync(userId: 1);

        owner.Home = Home();
        await _context.SaveChangesAsync(userId: 2);

        var rows = await HomeRowsAsync();
        rows.Select(r => r.PropertyName).Should().BeEquivalentTo("Home.Street", "Home.City", "Home.PostalCode");
        AssertRedacted(rows);
        AssertNoPlaintext(await RowsAsync(), HomeStreet, HomeCity, HomePostal);
    }

    [Fact]
    public async Task PiiNavigation_UpdatingOneOwnedColumn_RecordsItRedacted()
    {
        var owner = new PiiOwnerThing { Name = "Owner", Home = Home() };
        _context.Owners.Add(owner);
        await _context.SaveChangesAsync(userId: 1);

        owner.Home!.City = "Newtown";
        await _context.SaveChangesAsync(userId: 2);

        var row = (await HomeRowsAsync()).Should().ContainSingle().Subject;
        row.PropertyName.Should().Be("Home.City");
        row.OldValue.Should().Be(PiiRedactor.RedactedToken);
        row.NewValue.Should().Be(PiiRedactor.RedactedToken);
        AssertNoPlaintext(await RowsAsync(), HomeStreet, HomeCity, HomePostal, "Newtown");
    }

    [Fact]
    public async Task PiiNavigation_ReplacingTheOwnedInstance_RedactsEveryOwnedColumn()
    {
        var owner = new PiiOwnerThing { Name = "Owner", Home = Home() };
        _context.Owners.Add(owner);
        await _context.SaveChangesAsync(userId: 1);

        owner.Home = new ContactAddress { Street = "2 Other Road", City = "Elsewhere", PostalCode = "22222" };
        await _context.SaveChangesAsync(userId: 2);

        var rows = await HomeRowsAsync();
        rows.Select(r => r.PropertyName).Should().BeEquivalentTo("Home.Street", "Home.City", "Home.PostalCode");
        AssertRedacted(rows);
        AssertNoPlaintext(await RowsAsync(), HomeStreet, HomeCity, HomePostal, "2 Other Road", "Elsewhere", "22222");
    }

    [Fact]
    public async Task UnmarkedNavigation_OnTheSameOwner_StillRecordsPlainValues()
    {
        var owner = new PiiOwnerThing
        {
            Name = "Owner",
            Office = new ContactAddress { Street = "9 Office Park", City = "Worktown", PostalCode = "90000" },
        };
        _context.Owners.Add(owner);
        await _context.SaveChangesAsync(userId: 1);

        owner.Office = new ContactAddress { Street = "10 Office Park", City = "Busytown", PostalCode = "90001" };
        await _context.SaveChangesAsync(userId: 2);

        var rows = (await RowsAsync()).Where(r => r.Operation == "Modified").ToList();
        rows.Should().Contain(r => r.PropertyName == "Office.Street" && r.OldValue == "9 Office Park" && r.NewValue == "10 Office Park");
        rows.Should().Contain(r => r.PropertyName == "Office.City" && r.OldValue == "Worktown" && r.NewValue == "Busytown");
        rows.Should().Contain(r => r.PropertyName == "Office.PostalCode" && r.OldValue == "90000" && r.NewValue == "90001");
    }

    [Fact]
    public async Task UnmarkedNavigation_UpdatingOneOwnedColumn_RecordsThePlainValues()
    {
        var owner = new PiiOwnerThing
        {
            Name = "Owner",
            Office = new ContactAddress { Street = "9 Office Park", City = "Worktown", PostalCode = "90000" },
        };
        _context.Owners.Add(owner);
        await _context.SaveChangesAsync(userId: 1);

        owner.Office!.City = "Busytown";
        await _context.SaveChangesAsync(userId: 2);

        var row = (await RowsAsync()).Where(r => r.Operation == "Modified").Should().ContainSingle().Subject;
        row.PropertyName.Should().Be("Office.City");
        row.OldValue.Should().Be("Worktown");
        row.NewValue.Should().Be("Busytown");
    }

    // -- C2(a): the ITwoFactorUserState members are credentials --
    [Fact]
    public async Task TwoFactorMembers_Changed_RecordTheRedactionToken()
    {
        var account = new TwoFactorAccount { Name = "Account" };
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync(userId: 1);

        account.Name = "Renamed";
        account.TwoFactorSecret = "PROTECTED-SECRET-VALUE";
        account.IsTwoFactorEnabled = true;
        account.TwoFactorRecoveryCodeHashes = ["recovery-hash-aaaa", "recovery-hash-bbbb"];
        await _context.SaveChangesAsync(userId: 2);

        var rows = (await RowsAsync()).Where(r => r.Operation == "Modified").ToList();
        foreach (var member in new[]
        {
            nameof(ITwoFactorUserState.TwoFactorSecret),
            nameof(ITwoFactorUserState.IsTwoFactorEnabled),
            nameof(ITwoFactorUserState.TwoFactorRecoveryCodeHashes),
        })
        {
            var row = rows.Should().ContainSingle(r => r.PropertyName == member).Subject;
            row.OldValue.Should().Be(PiiRedactor.RedactedToken, member);
            row.NewValue.Should().Be(PiiRedactor.RedactedToken, member);
        }

        // Redaction is per member, not per entity: an ordinary column still records its values.
        var name = rows.Should().ContainSingle(r => r.PropertyName == nameof(TwoFactorAccount.Name)).Subject;
        name.OldValue.Should().Be("Account");
        name.NewValue.Should().Be("Renamed");

        AssertNoPlaintext(await RowsAsync(), "PROTECTED-SECRET-VALUE", "recovery-hash");
    }

    [Fact]
    public async Task TwoFactorMembers_AddedAndDeleted_NeverRecordTheSecretOrHashes()
    {
        var account = new TwoFactorAccount
        {
            Name = "Account",
            TwoFactorSecret = "PROTECTED-SECRET-VALUE",
            IsTwoFactorEnabled = true,
            TwoFactorRecoveryCodeHashes = ["recovery-hash-aaaa"],
        };
        _context.Accounts.Add(account);
        await _context.SaveChangesAsync(userId: 1);

        _context.Accounts.Remove(account);
        await _context.SaveChangesAsync(userId: 2);

        var rows = await RowsAsync();
        rows.Should().Contain(r => r.Operation == "Added").And.Contain(r => r.Operation == "Deleted");
        AssertNoPlaintext(rows, "PROTECTED-SECRET-VALUE", "recovery-hash");
    }

    // -- Helpers --
    private static ContactAddress Home() => new() { Street = HomeStreet, City = HomeCity, PostalCode = HomePostal };

    private static void AssertRedacted(List<AuditTrailEntry> rows)
    {
        foreach (var row in rows)
        {
            AllowedHomeValues.Should().Contain(row.OldValue, row.PropertyName);
            row.NewValue.Should().Be(PiiRedactor.RedactedToken, row.PropertyName);
        }
    }

    private static void AssertNoPlaintext(List<AuditTrailEntry> rows, params string[] values)
    {
        foreach (var row in rows)
        {
            string?[] everyColumn =
            [
                row.EntityType, row.EntityKey, row.PropertyName, row.OldValue, row.NewValue,
                row.Operation, row.CorrelationId, row.TenantId,
            ];
            everyColumn.Should().NotContain(
                column => column != null && values.Any(v => column.Contains(v, StringComparison.OrdinalIgnoreCase)),
                "a redacted value must never reach the trail in readable form");
        }
    }

    private Task<List<AuditTrailEntry>> RowsAsync() =>
        _context.TrailRows.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

    private async Task<List<AuditTrailEntry>> HomeRowsAsync()
    {
        var rows = await RowsAsync();
        return [.. rows.Where(IsHomeModification)];
    }

    private static bool IsHomeModification(AuditTrailEntry row) =>
        row.Operation == "Modified" && row.PropertyName?.StartsWith("Home.", StringComparison.Ordinal) == true;
}

/// <summary>An owned contact address with several columns and no marker of its own.</summary>
public sealed class ContactAddress
{
    public string Street { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string PostalCode { get; set; } = string.Empty;
}

/// <summary>
/// An opted-in owner with two navigations to the same owned type: <see cref="Home"/> is marked
/// personal data on the owner, <see cref="Office"/> is not.
/// </summary>
public sealed class PiiOwnerThing : IAuditedEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    [Pii]
    public ContactAddress? Home { get; set; }

    public ContactAddress? Office { get; set; }
}

/// <summary>
/// An opted-in account implementing <see cref="ITwoFactorUserState"/> the way an Identity module's
/// <c>User</c> would: the two-factor members carry no marker of any kind.
/// </summary>
public sealed class TwoFactorAccount : IAuditedEntity, ITwoFactorUserState
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsTwoFactorEnabled { get; set; }

    public string? TwoFactorSecret { get; set; }

    public IReadOnlyCollection<string> TwoFactorRecoveryCodeHashes { get; set; } = [];
}

/// <summary>
/// A SQLite context running the real interceptor pipeline (the <see cref="AuditTrailTestContext"/>
/// shape) over the owner and account entities above.
/// </summary>
public sealed class RedactionTestContext : ApplicationDbContext
{
    private RedactionTestContext(DbContextOptions<RedactionTestContext> options, IServiceProvider serviceProvider)
        : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
    {
    }

    public DbSet<PiiOwnerThing> Owners => Set<PiiOwnerThing>();

    public DbSet<TwoFactorAccount> Accounts => Set<TwoFactorAccount>();

    public DbSet<AuditTrailEntry> TrailRows => Set<AuditTrailEntry>();

    public static RedactionTestContext Create(TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AuditSaveChangesInterceptor(timeProvider));
        services.AddSingleton(_ => new DomainEventSaveChangesInterceptor(
            new Mock<IDomainEventDispatcher>().Object,
            new Mock<ILogger<DomainEventSaveChangesInterceptor>>().Object,
            new Mock<IOutboxSignal>().Object,
            timeProvider: TimeProvider.System));
        services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
        services.AddSingleton(new AuditTrailSaveChangesInterceptor(timeProvider));

        var options = new DbContextOptionsBuilder<RedactionTestContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        var context = new RedactionTestContext(options, services.BuildServiceProvider());
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<PiiOwnerThing>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.OwnsOne(e => e.Home);
            entity.OwnsOne(e => e.Office);
        });

        modelBuilder.Entity<TwoFactorAccount>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.TwoFactorRecoveryCodeHashes)
                .HasConversion(
                    v => string.Join(',', v),
                    s => s.Split(',', StringSplitOptions.RemoveEmptyEntries),
                    new ValueComparer<IReadOnlyCollection<string>>(
                        (a, b) => (a ?? Array.Empty<string>()).SequenceEqual(b ?? Array.Empty<string>()),
                        v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode(StringComparison.Ordinal))),
                        v => v.ToArray()));
        });

        // SQLite-portable copy of the production mapping (the AuditTrailTestContext one).
        modelBuilder.Entity<AuditTrailEntry>(entity =>
        {
            entity.ToTable("AuditTrailEntries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(256);
            entity.Property(e => e.EntityKey).IsRequired().HasMaxLength(128);
            entity.Property(e => e.PropertyName).HasMaxLength(128);
            entity.Property(e => e.Operation).IsRequired().HasMaxLength(16);
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.TenantId).HasMaxLength(64);
        });
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}
