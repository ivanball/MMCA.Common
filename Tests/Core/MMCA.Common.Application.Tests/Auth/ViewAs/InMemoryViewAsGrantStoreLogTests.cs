using AwesomeAssertions;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Testing.Support;

namespace MMCA.Common.Application.Tests.Auth.ViewAs;

/// <summary>
/// The in-memory grant store's support access log read and erasure step (ADR-137) mirror the EF
/// store's (<c>EFViewAsGrantStoreTests</c> in the Infrastructure tests runs the same four grants
/// against SQLite), so a workflow test written over this store sees what production would.
/// </summary>
public sealed class InMemoryViewAsGrantStoreLogTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryViewAsGrantStore _store = new();
    private readonly ViewAsGrant _expiredOld;
    private readonly ViewAsGrant _expiredRecent;
    private readonly ViewAsGrant _ended;
    private readonly ViewAsGrant _live;

    public InMemoryViewAsGrantStoreLogTests()
    {
        _expiredOld = Grant(2, 12, Now.AddDays(-1), "Dan@Staff.test", "erin@example.com");
        _expiredRecent = Grant(1, 10, Now.AddHours(-3), "ana@staff.test", "bob@example.com");
        _ended = Grant(1, 11, Now.AddHours(-2), "ana@staff.test", "carla@example.com");
        _ended.End(Now.AddHours(-2).AddMinutes(5));
        _live = Grant(2, 10, Now.AddMinutes(-5), "dan@staff.test", "bob@example.com");

        foreach (var grant in new[] { _expiredOld, _expiredRecent, _ended, _live })
        {
            _store.Seed(grant);
        }
    }

    // Spec A assertion 1 (in-memory mirror): newest first, paged, each filter narrows, status derived.
    [Fact]
    public async Task ListAsync_ReturnsNewestFirst_PagedWithTheTotal()
    {
        var first = await _store.ListAsync(new ViewAsGrantListRequest { PageNumber = 1, PageSize = 2 }, Now, TestContext.Current.CancellationToken);
        var second = await _store.ListAsync(new ViewAsGrantListRequest { PageNumber = 2, PageSize = 2 }, Now, TestContext.Current.CancellationToken);

        first.TotalCount.Should().Be(4);
        first.Items.Should().Equal(_live, _ended);
        second.Items.Should().Equal(_expiredRecent, _expiredOld);
    }

    [Fact]
    public async Task ListAsync_EachFilterNarrowsTheLog_AndTheStatusIsDerived()
    {
        (await ItemsAsync(new ViewAsGrantListRequest { Staff = "ANA@" })).Should().Equal(_ended, _expiredRecent);
        (await ItemsAsync(new ViewAsGrantListRequest { Viewed = "Bob" })).Should().Equal(_live, _expiredRecent);
        (await ItemsAsync(new ViewAsGrantListRequest { From = Now.AddHours(-2.5) })).Should().Equal(_live, _ended);
        (await ItemsAsync(new ViewAsGrantListRequest { To = Now.AddHours(-2.5) })).Should().Equal(_expiredRecent, _expiredOld);
        (await ItemsAsync(new ViewAsGrantListRequest { Status = ViewAsGrantStatus.Live })).Should().Equal(_live);
        (await ItemsAsync(new ViewAsGrantListRequest { Status = ViewAsGrantStatus.Ended })).Should().Equal(_ended);
        (await ItemsAsync(new ViewAsGrantListRequest { Status = ViewAsGrantStatus.Expired })).Should().Equal(_expiredRecent, _expiredOld);
        (await ItemsAsync(new ViewAsGrantListRequest { Staff = "dan", Status = ViewAsGrantStatus.Expired })).Should().Equal(_expiredOld);

        _live.StatusAt(Now).Should().Be(ViewAsGrantStatus.Live);
        _ended.StatusAt(Now).Should().Be(ViewAsGrantStatus.Ended);
        _expiredRecent.StatusAt(Now).Should().Be(ViewAsGrantStatus.Expired);
    }

    [Fact]
    public async Task ForgetUserAsync_BlanksThatAccountsSnapshotOnBothSides()
    {
        (await _store.ForgetUserAsync(10, TestContext.Current.CancellationToken)).Should().Be(2);
        (await _store.ForgetUserAsync(1, TestContext.Current.CancellationToken)).Should().Be(2);

        _live.TargetDisplay.Should().BeNull();
        _expiredRecent.TargetDisplay.Should().BeNull();
        _expiredRecent.StaffDisplay.Should().BeNull();
        _ended.StaffDisplay.Should().BeNull();
        _ended.TargetDisplay.Should().Be("carla@example.com");
        _live.StaffDisplay.Should().Be("dan@staff.test");
    }

    private static ViewAsGrant Grant(UserIdentifierType staff, UserIdentifierType target, DateTime grantedAt, string staffDisplay, string targetDisplay) =>
        ViewAsGrant.Create(staff, target, "Ticket 4411: schedule missing", grantedAt, TimeSpan.FromMinutes(15), staffDisplay, targetDisplay).Value!;

    private async Task<IReadOnlyList<ViewAsGrant>> ItemsAsync(ViewAsGrantListRequest request) =>
        (await _store.ListAsync(request, Now, TestContext.Current.CancellationToken)).Items;
}
