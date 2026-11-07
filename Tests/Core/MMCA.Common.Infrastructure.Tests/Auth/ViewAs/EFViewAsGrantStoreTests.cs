using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Infrastructure.Persistence.Auth;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Interceptors;
using MMCA.Common.Infrastructure.Persistence.Outbox.Processing;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using Moq;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Tests.Auth.ViewAs;

/// <summary>
/// The EF grant store's support access log read and its erasure step (ADR-137) against a real SQLite
/// database, so the filters, the ordering and the paging are the SQL EF actually generates. SQLite
/// compares text case-sensitively (as PostgreSQL does), which is what the case-insensitive email
/// filter has to survive.
/// </summary>
public sealed class EFViewAsGrantStoreTests : IAsyncDisposable
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly List<ApplicationDbContext> _contexts = [];

    // Spec A assertion 1: newest first, paged, each filter narrows, status derived at the read.
    [Fact]
    public async Task ListAsync_ReturnsNewestFirst_PagedWithTheTotal()
    {
        var (sut, ids) = await SeedAsync();

        var first = await sut.ListAsync(new ViewAsGrantListRequest { PageNumber = 1, PageSize = 2 }, Now, CancellationToken.None);
        var second = await sut.ListAsync(new ViewAsGrantListRequest { PageNumber = 2, PageSize = 2 }, Now, CancellationToken.None);

        first.TotalCount.Should().Be(4);
        first.Items.Select(g => g.Id).Should().Equal(ids.Live, ids.Ended);
        second.TotalCount.Should().Be(4);
        second.Items.Select(g => g.Id).Should().Equal(ids.ExpiredRecent, ids.ExpiredOld);
    }

    [Fact]
    public async Task ListAsync_EachFilterNarrowsTheLog()
    {
        var (sut, ids) = await SeedAsync();

        (await IdsAsync(sut, new ViewAsGrantListRequest { Staff = "ANA@" })).Should().Equal(
            [ids.Ended, ids.ExpiredRecent], "the staff filter is a case-insensitive contains on the staff email");
        (await IdsAsync(sut, new ViewAsGrantListRequest { Viewed = "Bob" })).Should().Equal(ids.Live, ids.ExpiredRecent);
        (await IdsAsync(sut, new ViewAsGrantListRequest { From = Now.AddHours(-2.5) })).Should().Equal(
            [ids.Live, ids.Ended], "From is inclusive of grants at or after it");
        (await IdsAsync(sut, new ViewAsGrantListRequest { To = Now.AddHours(-2.5) })).Should().Equal(
            [ids.ExpiredRecent, ids.ExpiredOld], "To is exclusive: grants strictly before it");
        (await IdsAsync(sut, new ViewAsGrantListRequest { Staff = "dan", Status = ViewAsGrantStatus.Expired })).Should().Equal(
            [ids.ExpiredOld], "filters combine with AND");
    }

    [Fact]
    public async Task ListAsync_TheStatusFilterIsDerivedFromEndAndExpiryAtTheReadInstant()
    {
        var (sut, ids) = await SeedAsync();

        (await IdsAsync(sut, new ViewAsGrantListRequest { Status = ViewAsGrantStatus.Live })).Should().Equal(ids.Live);
        (await IdsAsync(sut, new ViewAsGrantListRequest { Status = ViewAsGrantStatus.Ended })).Should().Equal(ids.Ended);
        (await IdsAsync(sut, new ViewAsGrantListRequest { Status = ViewAsGrantStatus.Expired })).Should().Equal(ids.ExpiredRecent, ids.ExpiredOld);

        var later = await sut.ListAsync(new ViewAsGrantListRequest { Status = ViewAsGrantStatus.Live }, Now.AddHours(1), CancellationToken.None);
        later.Items.Should().BeEmpty("the live grant has expired an hour later");
    }

    [Fact]
    public async Task ListAsync_ClampsThePageSize()
    {
        var (sut, _) = await SeedAsync();

        var page = await sut.ListAsync(new ViewAsGrantListRequest { PageSize = 10_000 }, Now, CancellationToken.None);

        page.Items.Should().HaveCount(4);
        page.TotalCount.Should().Be(4);
    }

    // Spec A assertion 3 (erasure, EF half): the erased account's snapshot is blanked on every grant
    // naming it, and the blanking is persisted by the caller's own save.
    [Fact]
    public async Task ForgetUserAsync_BlanksThatAccountsSnapshotOnBothSides_AndTheCallersSavePersistsIt()
    {
        var (sut, ids) = await SeedAsync();

        var changed = await sut.ForgetUserAsync(10, CancellationToken.None);
        await StoreContext.SaveChangesAsync(CancellationToken.None);

        changed.Should().Be(2, "account 10 is the viewed account of two grants");
        var rows = await NewContext().Set<ViewAsGrant>().AsNoTracking().ToDictionaryAsync(g => g.Id, CancellationToken.None);
        rows[ids.Live].TargetDisplay.Should().BeNull();
        rows[ids.ExpiredRecent].TargetDisplay.Should().BeNull();
        rows[ids.Live].StaffDisplay.Should().Be("dan@staff.test", "the other party keeps its snapshot");
        rows[ids.Ended].TargetDisplay.Should().Be("carla@example.com", "a grant not naming the account is untouched");
        rows[ids.Live].Reason.Should().NotBeNullOrEmpty("the access itself stays on record");
    }

    [Fact]
    public async Task ForgetUserAsync_ForAStaffAccount_BlanksTheStaffSnapshot()
    {
        var (sut, ids) = await SeedAsync();

        await sut.ForgetUserAsync(1, CancellationToken.None);
        await StoreContext.SaveChangesAsync(CancellationToken.None);

        var rows = await NewContext().Set<ViewAsGrant>().AsNoTracking().ToDictionaryAsync(g => g.Id, CancellationToken.None);
        rows[ids.Ended].StaffDisplay.Should().BeNull();
        rows[ids.ExpiredRecent].StaffDisplay.Should().BeNull();
        rows[ids.Live].StaffDisplay.Should().Be("dan@staff.test");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }

    private ApplicationDbContext StoreContext => _contexts[0];

    private static async Task<IReadOnlyList<Guid>> IdsAsync(EFViewAsGrantStore sut, ViewAsGrantListRequest request) =>
        [.. (await sut.ListAsync(request, Now, CancellationToken.None)).Items.Select(g => g.Id)];

    /// <summary>
    /// Four grants: staff 1 (ana) viewed 10 (bob) and 11 (carla); staff 2 (dan) viewed 10 (bob) and
    /// 12 (erin). One live, one ended early, two expired.
    /// </summary>
    private async Task<(EFViewAsGrantStore Sut, SeededIds Ids)> SeedAsync()
    {
        await _connection.OpenAsync(CancellationToken.None);
        var storeContext = NewContext();

        var expiredOld = Grant(2, 12, Now.AddDays(-1), "Dan@Staff.test", "erin@example.com");
        var expiredRecent = Grant(1, 10, Now.AddHours(-3), "ana@staff.test", "bob@example.com");
        var ended = Grant(1, 11, Now.AddHours(-2), "ana@staff.test", "carla@example.com");
        ended.End(Now.AddHours(-2).AddMinutes(5));
        var live = Grant(2, 10, Now.AddMinutes(-5), "dan@staff.test", "bob@example.com");

        var seed = NewContext();
        seed.Set<ViewAsGrant>().AddRange(expiredOld, expiredRecent, ended, live);
        await seed.SaveChangesAsync(CancellationToken.None);

        var factory = new Mock<IDbContextFactory>();
        factory.Setup(f => f.GetDbContext(It.IsAny<DataSourceKey>())).Returns(storeContext);
        factory.Setup(f => f.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => storeContext.SaveChangesAsync(ct));

        var sut = new EFViewAsGrantStore(
            factory.Object,
            new EmptyEntityDataSourceRegistry(),
            Mock.Of<IDataSourceResolver>(),
            Options.Create(new ViewAsSettings { Enabled = true }));

        return (sut, new SeededIds(live.Id, ended.Id, expiredRecent.Id, expiredOld.Id));
    }

    private static ViewAsGrant Grant(UserIdentifierType staff, UserIdentifierType target, DateTime grantedAt, string staffDisplay, string targetDisplay) =>
        ViewAsGrant.Create(staff, target, "Ticket 4411: schedule missing", grantedAt, Lifetime, staffDisplay, targetDisplay).Value!;

    private GrantTestContext NewContext()
    {
        var context = GrantTestContext.Create(_connection);
        _contexts.Add(context);
        return context;
    }

    private sealed record SeededIds(Guid Live, Guid Ended, Guid ExpiredRecent, Guid ExpiredOld);

    private sealed class GrantTestContext : ApplicationDbContext
    {
        private GrantTestContext(DbContextOptions<GrantTestContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider, new NullAssemblyProvider(), TestPhysicalDataSources.Sqlite())
        {
        }

        public static GrantTestContext Create(SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<GrantTestContext>()
                .UseSqlite(connection)
                .Options;

            var context = new GrantTestContext(options, BuildContextServices());
            context.Database.EnsureCreated();
            return context;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyViewAsGrantConfiguration(schema: null);

        private static ServiceProvider BuildContextServices()
        {
            var services = new ServiceCollection();
            services.AddSingleton(new AuditSaveChangesInterceptor(TimeProvider.System));
            services.AddSingleton(_ => new DomainEventSaveChangesInterceptor(
                Mock.Of<IDomainEventDispatcher>(),
                Mock.Of<ILogger<DomainEventSaveChangesInterceptor>>(),
                Mock.Of<IOutboxSignal>(),
                timeProvider: TimeProvider.System));
            services.AddSingleton<IEntityDataSourceRegistry>(new EmptyEntityDataSourceRegistry());
            return services.BuildServiceProvider();
        }
    }

    private sealed class NullAssemblyProvider : IEntityConfigurationAssemblyProvider
    {
        public IReadOnlyList<Assembly> GetConfigurationAssemblies() => [];
    }
}
