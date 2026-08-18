using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Domain.Notifications.PushNotifications;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// The Application-tier projector test proves the projected VALUES equal the mapper's, but it runs
/// the projection in memory, where anything compiles. This tier proves the other half: that a real
/// provider TRANSLATES the projection into SQL, including the enum-to-string conversion the mapper
/// does with a method call. An untranslatable projection would only surface at runtime in a host.
/// </summary>
public sealed class PushNotificationProjectionTranslationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ProjectionTestDbContext _context;
    private readonly PushNotificationDTOProjector _projector = new();
    private readonly PushNotificationDTOMapper _mapper = new();

    public PushNotificationProjectionTranslationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ProjectionTestDbContext(
            new DbContextOptionsBuilder<ProjectionTestDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        Seed();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private void Seed()
    {
        var pending = PushNotification.Create("First", "Body one", sentByUserId: 1, recipientCount: 5).Value!;
        var sent = PushNotification.Create("Second", "Body two", sentByUserId: 2, recipientCount: 9, scopeKey: "event:2").Value!;
        sent.MarkAsSent();
        var failed = PushNotification.Create("Third", "Body three", sentByUserId: 3, recipientCount: 1).Value!;
        failed.MarkAsFailed();

        _context.AddRange(pending, sent, failed);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    [Fact]
    public void ProjectTo_TranslatesToSql()
    {
        var sql = _projector.ProjectTo(_context.PushNotifications.AsNoTracking()).ToQueryString();

        sql.Should().Contain("SELECT");
        sql.Should().NotContain("*", "a projection exists to select the DTO's columns, not every column");
    }

    [Fact]
    public async Task ProjectTo_MaterializesTheSameValuesAsTheMapper()
    {
        var projected = await _projector.ProjectTo(_context.PushNotifications.AsNoTracking())
            .OrderBy(d => d.Id)
            .ToListAsync();

        var entities = await _context.PushNotifications.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        var mapped = _mapper.MapToDTOs(entities);

        projected.Should().BeEquivalentTo(mapped);
    }

    [Fact]
    public async Task ProjectTo_RendersEveryStatusAsItsEnumName()
    {
        var statuses = await _projector.ProjectTo(_context.PushNotifications.AsNoTracking())
            .OrderBy(d => d.Id)
            .Select(d => d.Status)
            .ToListAsync();

        statuses.Should().Equal(
            nameof(PushNotificationStatus.Pending),
            nameof(PushNotificationStatus.Sent),
            nameof(PushNotificationStatus.Failed));
    }

    [Fact]
    public async Task ProjectTo_StaysComposable()
    {
        // Composing after the projection is what makes it a pushdown rather than a materialize-then-map.
        var titles = await _projector.ProjectTo(_context.PushNotifications.AsNoTracking())
            .Where(d => d.RecipientCount > 1)
            .OrderByDescending(d => d.RecipientCount)
            .Select(d => d.Title)
            .ToListAsync();

        titles.Should().Equal("Second", "First");
    }

    /// <summary>
    /// A minimal SQLite-mappable context over the notification aggregate. The production
    /// configuration is SQL Server specific (schema plus a bracketed filtered index), so the mapping
    /// is declared here, but the ONE detail that matters for the projection is kept: the status is
    /// stored through a string conversion, exactly as in production.
    /// </summary>
    public sealed class ProjectionTestDbContext(DbContextOptions options) : DbContext(options)
    {
        /// <summary>Gets the notification set.</summary>
        public DbSet<PushNotification> PushNotifications => Set<PushNotification>();

        /// <inheritdoc />
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PushNotification>(b =>
            {
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).ValueGeneratedOnAdd();
                b.Property(e => e.Title);
                b.Property(e => e.Body);
                b.Property(e => e.ScopeKey);
                b.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            });
    }
}
