using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Domain.Notifications.UserNotifications;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Notifications;

/// <summary>
/// The mark-all handler issues ONE set-based update (L49) instead of loading and tracking every
/// unread row. These tests capture the predicate and the property assignments handed to
/// <c>ExecuteUpdateAsync</c> and evaluate them in memory; the persisted effect against a real EF
/// context is covered in the Infrastructure tier (MarkAllNotificationsReadHandlerTrackingTests).
/// </summary>
public sealed class MarkAllNotificationsReadHandlerTests
{
    // -- One set-based update, no tracked save -- (inverted from ..._MarksAllAsReadAndSaves, L49)
    [Fact]
    public async Task HandleAsync_WithUnreadNotifications_MarksThemInOneSetBasedUpdateWithoutASave()
    {
        var harness = new Harness();
        List<UserNotification> rows =
        [
            Unread(userId: 42, pushNotificationId: 1),
            Unread(userId: 42, pushNotificationId: 2),
            Unread(userId: 42, pushNotificationId: 3),
            Read(userId: 42, pushNotificationId: 4),
            Unread(userId: 7, pushNotificationId: 5),
        ];

        Result result = await harness.Sut.HandleAsync(new MarkAllNotificationsReadCommand(UserId: 42));

        result.IsSuccess.Should().BeTrue();
        harness.Predicate.Should().NotBeNull("the handler must mark the rows with one set-based update");
        rows.Where(harness.Predicate!.Compile()).Should().BeEquivalentTo(rows.Take(3));
        harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Nothing is loaded or saved, whatever the row count -- (inverted from ..._SkipsSave, L49)
    [Fact]
    public async Task HandleAsync_WhenNoUnreadNotifications_IssuesTheUpdateAndNeverSaves()
    {
        var harness = new Harness(affectedRows: 0);

        Result result = await harness.Sut.HandleAsync(new MarkAllNotificationsReadCommand(UserId: 42));

        result.IsSuccess.Should().BeTrue();
        harness.Predicate.Should().NotBeNull();
        harness.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Returns success even with no unread ──
    [Fact]
    public async Task HandleAsync_WhenNoUnreadNotifications_ReturnsSuccess()
    {
        var harness = new Harness(affectedRows: 0);

        Result result = await harness.Sut.HandleAsync(new MarkAllNotificationsReadCommand(UserId: 42));

        result.IsSuccess.Should().BeTrue();
    }

    // -- Read time is stamped from the injected clock -- (inverted to read the assignments, L49)
    [Fact]
    public async Task HandleAsync_WithUnreadNotifications_StampsReadOnFromInjectedClock()
    {
        var readInstant = new DateTimeOffset(2026, 6, 26, 14, 30, 0, TimeSpan.Zero);
        var harness = new Harness(timeProvider: new FixedTimeProvider(readInstant));

        Result result = await harness.Sut.HandleAsync(new MarkAllNotificationsReadCommand(UserId: 42));

        result.IsSuccess.Should().BeTrue();
        harness.Assignments.Should().HaveCount(2);
        harness.Assignments[nameof(UserNotification.IsRead)].Should().Be(true);
        harness.Assignments[nameof(UserNotification.ReadOn)].Should().Be(readInstant.UtcDateTime);
    }

    // ── Scope filtering ──
    [Fact]
    public async Task HandleAsync_WithoutScope_NeverJoinsPushNotification()
    {
        // Mirrors the unread count: an unconditional join would drag PushNotification's soft-delete
        // global filter into the legacy command and silently change which rows it clears.
        var harness = new Harness();

        Result result = await harness.Sut.HandleAsync(new MarkAllNotificationsReadCommand(UserId: 42));

        result.IsSuccess.Should().BeTrue();
        harness.UnitOfWork.Verify(x => x.GetRepository<PushNotification, PushNotificationIdentifierType>(), Times.Never);
    }

    // These two scope tests pin the PREDICATE only, evaluated over in-memory rows. The persisted
    // effect lives in the Infrastructure tier, where the handler runs against a real EF context.
    [Fact]
    public async Task HandleAsync_WithScope_MarksMatchingAndUnscopedOnly()
    {
        var harness = new Harness();
        var notifications = harness.WithScopedPushNotifications();

        Result result = await harness.Sut.HandleAsync(new MarkAllNotificationsReadCommand(UserId: 1, ScopeKey: "event:2"));

        result.IsSuccess.Should().BeTrue();
        var selected = notifications.Where(harness.Predicate!.Compile()).ToList();
        selected.Should().Contain(notifications[0], "the unscoped notification is visible under every scope");
        selected.Should().NotContain(notifications[1], "an \"event:1\" notification is invisible to an \"event:2\" client");
        selected.Should().Contain(notifications[2]);
    }

    [Fact]
    public async Task HandleAsync_WithoutScope_MarksEveryUnreadNotification()
    {
        var harness = new Harness();
        var notifications = harness.WithScopedPushNotifications();

        Result result = await harness.Sut.HandleAsync(new MarkAllNotificationsReadCommand(UserId: 1));

        result.IsSuccess.Should().BeTrue();
        notifications.Where(harness.Predicate!.Compile()).Should().HaveCount(3);
    }

    // ── Helpers ──
    private static UserNotification Unread(UserIdentifierType userId, PushNotificationIdentifierType pushNotificationId) =>
        UserNotification.Create(userId, pushNotificationId).Value!;

    private static UserNotification Read(UserIdentifierType userId, PushNotificationIdentifierType pushNotificationId)
    {
        var notification = Unread(userId, pushNotificationId);
        notification.MarkAsRead(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return notification;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>The handler over mocks that capture the one set-based update it issues.</summary>
    private sealed class Harness
    {
        private readonly Mock<IRepository<UserNotification, UserNotificationIdentifierType>> _repository = new();

        public Harness(int affectedRows = 3, TimeProvider? timeProvider = null)
        {
            UnitOfWork.Setup(x => x.GetRepository<UserNotification, UserNotificationIdentifierType>())
                .Returns(_repository.Object);
            _repository.Setup(x => x.Table).Returns(Enumerable.Empty<UserNotification>().AsQueryable());
            _repository
                .Setup(x => x.ExecuteUpdateAsync(
                    It.IsAny<Expression<Func<UserNotification, bool>>>(),
                    It.IsAny<Action<IUpdatePropertySetter<UserNotification>>>(),
                    It.IsAny<CancellationToken>()))
                .Callback((Expression<Func<UserNotification, bool>> where, Action<IUpdatePropertySetter<UserNotification>> setters, CancellationToken _) =>
                {
                    Predicate = where;
                    var recorder = new RecordingSetter();
                    setters(recorder);
                    Assignments = recorder.Values;
                })
                .ReturnsAsync(affectedRows);

            Sut = new MarkAllNotificationsReadHandler(UnitOfWork.Object, timeProvider ?? TimeProvider.System);
        }

        public Mock<IUnitOfWork> UnitOfWork { get; } = new();

        public MarkAllNotificationsReadHandler Sut { get; }

        public Expression<Func<UserNotification, bool>>? Predicate { get; private set; }

        public Dictionary<string, object?> Assignments { get; private set; } = new(StringComparer.Ordinal);

        /// <summary>Three unread rows for user 1 over push notifications scoped none, "event:1", "event:2".</summary>
        public IReadOnlyList<UserNotification> WithScopedPushNotifications()
        {
            List<PushNotification> pushNotifications =
            [
                Push(id: 1, scopeKey: null),
                Push(id: 2, scopeKey: "event:1"),
                Push(id: 3, scopeKey: "event:2"),
            ];

            var pushRepository = new Mock<IRepository<PushNotification, PushNotificationIdentifierType>>();
            pushRepository.Setup(x => x.Table).Returns(pushNotifications.AsQueryable());
            UnitOfWork.Setup(x => x.GetRepository<PushNotification, PushNotificationIdentifierType>())
                .Returns(pushRepository.Object);

            return [.. pushNotifications.Select(pn => Unread(userId: 1, pushNotificationId: pn.Id))];
        }
        /// <summary>
        /// Builds a notification that looks persisted. <c>Id</c> is <c>required init</c> and the factory
        /// leaves it at its default, so the identifier the scope test needs is written back through
        /// reflection rather than by opening the entity up with a test-only setter.
        /// </summary>
        private static PushNotification Push(PushNotificationIdentifierType id, string? scopeKey)
        {
            PushNotification notification = PushNotification
                .Create("Title", "Body", sentByUserId: 1, recipientCount: 1, scopeKey: scopeKey).Value!;
            typeof(PushNotification).GetProperty(nameof(PushNotification.Id))!.SetValue(notification, id);
            return notification;
        }
    }

    /// <summary>Records each fixed-value assignment by property name.</summary>
    private sealed class RecordingSetter : IUpdatePropertySetter<UserNotification>
    {
        public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

        public IUpdatePropertySetter<UserNotification> Set<TProperty>(
            Expression<Func<UserNotification, TProperty>> property,
            TProperty value)
        {
            Values[((MemberExpression)property.Body).Member.Name] = value;
            return this;
        }

        public IUpdatePropertySetter<UserNotification> Set<TProperty>(
            Expression<Func<UserNotification, TProperty>> property,
            Expression<Func<UserNotification, TProperty>> valueFactory) =>
            throw new NotSupportedException("The handler assigns fixed values only.");
    }
}
