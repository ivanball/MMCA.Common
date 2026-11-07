using AwesomeAssertions;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.Domain.Tests.Auth;

/// <summary>
/// The support access log's domain half of <see cref="ViewAsGrant"/> (ADR-137): the display snapshot
/// a grant records, the status derived from it, and the erasure step that blanks one party's snapshot.
/// </summary>
public sealed class ViewAsGrantTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_WithDisplays_StoresThemTrimmedLowerCasedAndBounded()
    {
        var longDisplay = new string('A', ViewAsGrant.DisplayMaxLength + 40);

        var grant = ViewAsGrant.Create(1, 2, "Ticket 4411: schedule missing", Now, TimeSpan.FromMinutes(15), "  Staff@Example.COM ", longDisplay).Value!;

        grant.StaffDisplay.Should().Be("staff@example.com");
        grant.TargetDisplay.Should().HaveLength(ViewAsGrant.DisplayMaxLength).And.MatchRegex("^a+$");
    }

    [Fact]
    public void Create_WithoutDisplays_RecordsNone()
    {
        var grant = ViewAsGrant.Create(1, 2, "Ticket 4411: schedule missing", Now, TimeSpan.FromMinutes(15)).Value!;

        grant.StaffDisplay.Should().BeNull();
        grant.TargetDisplay.Should().BeNull();
        ViewAsGrant.NormalizeDisplay("   ").Should().BeNull("a blank snapshot is no snapshot");
    }

    [Fact]
    public void StatusAt_IsLiveUntilExpiry_ExpiredAfter_AndEndedOnceEnded()
    {
        var grant = ViewAsGrant.Create(1, 2, "Ticket 4411: schedule missing", Now, TimeSpan.FromMinutes(15)).Value!;

        grant.StatusAt(Now.AddMinutes(14)).Should().Be(ViewAsGrantStatus.Live);
        grant.StatusAt(Now.AddMinutes(15)).Should().Be(ViewAsGrantStatus.Expired);

        grant.End(Now.AddMinutes(3));
        grant.StatusAt(Now.AddMinutes(4)).Should().Be(ViewAsGrantStatus.Ended);
        grant.StatusAt(Now.AddHours(1)).Should().Be(ViewAsGrantStatus.Ended, "an ended grant stays ended past its expiry");
    }

    [Fact]
    public void ForgetDisplayOf_BlanksOnlyThatPartysSnapshot()
    {
        var grant = ViewAsGrant.Create(1, 2, "Ticket 4411: schedule missing", Now, TimeSpan.FromMinutes(15), "staff@example.com", "target@example.com").Value!;

        grant.ForgetDisplayOf(3).Should().BeFalse("the grant does not name account 3");
        grant.ForgetDisplayOf(2).Should().BeTrue();
        grant.TargetDisplay.Should().BeNull();
        grant.StaffDisplay.Should().Be("staff@example.com");

        grant.ForgetDisplayOf(1).Should().BeTrue();
        grant.StaffDisplay.Should().BeNull();
        grant.ForgetDisplayOf(1).Should().BeFalse("nothing is left to blank");
    }
}
