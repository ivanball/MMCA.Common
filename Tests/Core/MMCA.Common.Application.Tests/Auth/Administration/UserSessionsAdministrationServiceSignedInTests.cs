using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Administration;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.Administration;

/// <summary>
/// Pins the two roster queries on <see cref="UserSessionsAdministrationService"/> a consumer's user
/// list builds its "signed in" filter and column from. Both delegate to one store query each, with
/// the injected clock's now passed down so the live-session predicate runs in the database; the
/// store's answer is handed back as is, and an empty count request never reaches the store.
/// </summary>
public sealed class UserSessionsAdministrationServiceSignedInTests
{
    private const UserIdentifierType UserA = 42;
    private const UserIdentifierType UserB = 43;
    private const UserIdentifierType UserC = 44;

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IRefreshSessionStore> _store = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _time = new(FixedNow);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UserSessionsAdministrationService CreateSut() => new(_store.Object, _time);

    [Fact]
    public async Task GetSignedInUserIdsAsync_ReturnsTheStoresLiveSessionUsers()
    {
        _store
            .Setup(s => s.GetUserIdsWithLiveSessionsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([UserA, UserB]);

        var result = await CreateSut().GetSignedInUserIdsAsync(Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo([UserA, UserB]);
    }

    [Fact]
    public async Task GetSignedInUserIdsAsync_PassesTheInjectedClocksNowToTheStore()
    {
        _time.Advance(TimeSpan.FromHours(3));
        _store
            .Setup(s => s.GetUserIdsWithLiveSessionsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await CreateSut().GetSignedInUserIdsAsync(Ct);

        _store.Verify(
            s => s.GetUserIdsWithLiveSessionsAsync(FixedNow.UtcDateTime.AddHours(3), It.IsAny<CancellationToken>()),
            Times.Once,
            "the expiry cut-off is the TimeProvider's now, evaluated in the store's one query");
    }

    [Fact]
    public async Task GetSignedInUserIdsAsync_WithNobodySignedIn_SucceedsWithAnEmptyList()
    {
        _store
            .Setup(s => s.GetUserIdsWithLiveSessionsAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await CreateSut().GetSignedInUserIdsAsync(Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task CountLiveSessionsAsync_ReturnsTheStoresCountsPerUser()
    {
        _store
            .Setup(s => s.CountLiveSessionsByUserAsync(
                It.IsAny<IReadOnlyCollection<UserIdentifierType>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<UserIdentifierType, int> { [UserA] = 2, [UserB] = 1 });

        var result = await CreateSut().CountLiveSessionsAsync([UserA, UserB, UserC], Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new Dictionary<UserIdentifierType, int> { [UserA] = 2, [UserB] = 1 });
        result.Value!.Should().NotContainKey(UserC, "a requested user with no live session is absent, not a zero entry");
    }

    [Fact]
    public async Task CountLiveSessionsAsync_PassesTheRequestedUsersAndTheClocksNowToTheStore()
    {
        _store
            .Setup(s => s.CountLiveSessionsByUserAsync(
                It.IsAny<IReadOnlyCollection<UserIdentifierType>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<UserIdentifierType, int>());

        await CreateSut().CountLiveSessionsAsync([UserA, UserB], Ct);

        _store.Verify(
            s => s.CountLiveSessionsByUserAsync(
                It.Is<IReadOnlyCollection<UserIdentifierType>>(ids => ids.Order().SequenceEqual(new[] { UserA, UserB })),
                FixedNow.UtcDateTime,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "one grouped store query for the whole page, never one per user");
    }

    [Fact]
    public async Task CountLiveSessionsAsync_WithNoUsers_ReturnsEmptyWithoutQueryingTheStore()
    {
        // The strict mock has no setup, so any store call throws.
        var result = await CreateSut().CountLiveSessionsAsync([], Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        _store.VerifyNoOtherCalls();
    }
}
