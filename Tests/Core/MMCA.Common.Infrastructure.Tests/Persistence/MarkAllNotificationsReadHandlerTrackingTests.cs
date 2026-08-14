using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Runs the REAL <see cref="MarkAllNotificationsReadHandler"/> against a real EF Core context
/// (SQLite in-memory, real <see cref="EFRepository{TEntity, TIdentifierType}"/> pair, real
/// <see cref="EFQueryableExecutor"/>), because the mock-based Application tests cannot observe
/// change tracking at all: their in-memory queryables have no change tracker, so a scoped
/// mark-all that composes over an <c>AsNoTracking()</c> source still looks correct there while
/// persisting nothing. Every assertion re-queries through a FRESH context, so only what actually
/// reached the database counts.
/// </summary>
public sealed class MarkAllNotificationsReadHandlerTrackingTests : IDisposable
{
    private const UserIdentifierType RecipientUserId = 7;

    private readonly SqliteConnection _connection;
    private readonly NotificationTestDbContext _context;
    private readonly MarkAllNotificationsReadHandler _sut;

    public MarkAllNotificationsReadHandlerTrackingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = CreateContext();
        _context.Database.EnsureCreated();

        var userNotificationRepository = new EFRepository<UserNotification, UserNotificationIdentifierType>(_context);
        var pushNotificationRepository = new EFRepository<PushNotification, PushNotificationIdentifierType>(_context);

        // Only the repository lookup and the save are stubbed; both repositories and the executor are
        // the production types over the production DbContext, so tracking behaves exactly as it does
        // in a host.
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.GetRepository<UserNotification, UserNotificationIdentifierType>())
            .Returns(userNotificationRepository);
        unitOfWork.Setup(x => x.GetRepository<PushNotification, PushNotificationIdentifierType>())
            .Returns(pushNotificationRepository);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => _context.SaveChangesAsync(token));

        _sut = new MarkAllNotificationsReadHandler(unitOfWork.Object, new EFQueryableExecutor(), TimeProvider.System);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── Scoped path (the tracking regression) ──
    [Fact]
    public async Task HandleAsync_WithScope_PersistsTheMarksForTheScopedAndUnscopedRows()
    {
        SeededIds seeded = await SeedThreeUnreadNotificationsAsync();

        Result result = await _sut.HandleAsync(
            new MarkAllNotificationsReadCommand(RecipientUserId, ScopeKey: "event:2"));

        result.IsSuccess.Should().BeTrue();
        (await IsReadInTheDatabaseAsync(seeded.ScopedTwo)).Should().BeTrue(
            because: "the scoped join must keep the query tracked, otherwise MarkAsRead never reaches the database");
        (await IsReadInTheDatabaseAsync(seeded.Unscoped)).Should().BeTrue(
            because: "an unscoped notification is visible under every scope");
        (await IsReadInTheDatabaseAsync(seeded.ScopedOne)).Should().BeFalse(
            because: "an \"event:1\" notification is invisible to an \"event:2\" client");
    }

    [Fact]
    public async Task HandleAsync_WithScope_LeavesNoUnreadRowsInScope()
    {
        await SeedThreeUnreadNotificationsAsync();

        await _sut.HandleAsync(new MarkAllNotificationsReadCommand(RecipientUserId, ScopeKey: "event:2"));

        await using NotificationTestDbContext verification = CreateContext();
        var unreadCount = await verification.UserNotifications
            .AsNoTracking()
            .CountAsync(un => un.UserId == RecipientUserId && !un.IsRead);

        unreadCount.Should().Be(1, because: "only the out-of-scope \"event:1\" row may survive as unread");
    }

    // ── Legacy path (no scope) ──
    [Fact]
    public async Task HandleAsync_WithoutScope_PersistsTheMarksForEveryRow()
    {
        SeededIds seeded = await SeedThreeUnreadNotificationsAsync();

        Result result = await _sut.HandleAsync(new MarkAllNotificationsReadCommand(RecipientUserId));

        result.IsSuccess.Should().BeTrue();
        (await IsReadInTheDatabaseAsync(seeded.ScopedOne)).Should().BeTrue();
        (await IsReadInTheDatabaseAsync(seeded.ScopedTwo)).Should().BeTrue();
        (await IsReadInTheDatabaseAsync(seeded.Unscoped)).Should().BeTrue();
    }

    // ── Helpers ──
    private NotificationTestDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<NotificationTestDbContext>().UseSqlite(_connection).Options);

    /// <summary>
    /// Seeds one recipient with three unread notifications whose parent push notifications carry
    /// the scopes "event:1", "event:2" and none, then clears the change tracker so the handler
    /// starts from the same cold state a request-scoped context would.
    /// </summary>
    private async Task<SeededIds> SeedThreeUnreadNotificationsAsync()
    {
        PushNotification scopedOne = CreatePush("event:1");
        PushNotification scopedTwo = CreatePush("event:2");
        PushNotification unscoped = CreatePush(scopeKey: null);

        _context.PushNotifications.AddRange(scopedOne, scopedTwo, unscoped);
        await _context.SaveChangesAsync();

        _context.UserNotifications.AddRange(
            UserNotification.Create(RecipientUserId, scopedOne.Id).Value!,
            UserNotification.Create(RecipientUserId, scopedTwo.Id).Value!,
            UserNotification.Create(RecipientUserId, unscoped.Id).Value!);
        await _context.SaveChangesAsync();

        _context.ChangeTracker.Clear();

        return new SeededIds(scopedOne.Id, scopedTwo.Id, unscoped.Id);
    }

    private async Task<bool> IsReadInTheDatabaseAsync(PushNotificationIdentifierType pushNotificationId)
    {
        await using NotificationTestDbContext verification = CreateContext();
        return await verification.UserNotifications
            .AsNoTracking()
            .Where(un => un.UserId == RecipientUserId && un.PushNotificationId == pushNotificationId)
            .Select(un => un.IsRead)
            .SingleAsync();
    }

    private static PushNotification CreatePush(string? scopeKey) =>
        PushNotification.Create("Title", "Body", sentByUserId: 1, recipientCount: 3, scopeKey: scopeKey).Value!;

    private sealed record SeededIds(
        PushNotificationIdentifierType ScopedOne,
        PushNotificationIdentifierType ScopedTwo,
        PushNotificationIdentifierType Unscoped);

    /// <summary>
    /// A minimal context over the two real notification aggregates. The production configurations
    /// are SQL Server specific (schemas plus bracketed filtered indexes), so the mapping is declared
    /// here instead, exactly as the neighbouring EF repository integration tests do.
    /// </summary>
    public sealed class NotificationTestDbContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<PushNotification> PushNotifications => Set<PushNotification>();

        public DbSet<UserNotification> UserNotifications => Set<UserNotification>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PushNotification>(b =>
            {
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).ValueGeneratedOnAdd();
                b.Property(e => e.Title);
                b.Property(e => e.Body);
                b.Property(e => e.ScopeKey);
            });
            modelBuilder.Entity<UserNotification>(b =>
            {
                b.HasKey(e => e.Id);
                b.Property(e => e.Id).ValueGeneratedOnAdd();
                b.Property(e => e.UserId);
                b.Property(e => e.PushNotificationId);
                b.Property(e => e.IsRead);
                b.Property(e => e.ReadOn);
            });
        }
    }
}
