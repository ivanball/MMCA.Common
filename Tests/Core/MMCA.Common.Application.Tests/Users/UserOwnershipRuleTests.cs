using AwesomeAssertions;
using MMCA.Common.Application.Users;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Exercises the shared self-service authorization rule the account-deletion and data-export use
/// cases both apply (it was written out four times across the two apps before the hoist).
/// </summary>
public sealed class UserOwnershipRuleTests
{
    [Fact]
    public void CheckOwnership_WhenCallerIsOwner_Allows()
    {
        var error = Check(userId: 7, currentUserId: 7, callerHasPrivilegedRole: false);

        error.Should().BeNull();
    }

    [Fact]
    public void CheckOwnership_WhenCallerIsPrivileged_Allows()
    {
        var error = Check(userId: 7, currentUserId: 9, callerHasPrivilegedRole: true);

        error.Should().BeNull();
    }

    [Fact]
    public void CheckOwnership_WhenCallerIsNeither_ReturnsForbiddenWithSuppliedPayload()
    {
        var error = Check(userId: 7, currentUserId: 9, callerHasPrivilegedRole: false);

        error.Should().NotBeNull();
        error!.Type.Should().Be(ErrorType.Forbidden);
        error.Code.Should().Be("User.ExportForbidden");
        error.Message.Should().Be("You can only export your own account data.");
        error.Source.Should().Be("ExportUserDataHandler");
        error.Target.Should().Be("UserId");
    }

    [Fact]
    public void CheckOwnership_WhenRequestIsNull_Throws()
    {
        var act = () => UserOwnershipRule.CheckOwnership(null!, false, "c", "m", "s");

        act.Should().Throw<ArgumentNullException>();
    }

    private static Error? Check(
        UserIdentifierType userId,
        UserIdentifierType currentUserId,
        bool callerHasPrivilegedRole) =>
        UserOwnershipRule.CheckOwnership(
            new TestDeleteUserCommand(userId, currentUserId, "Attendee"),
            callerHasPrivilegedRole,
            code: "User.ExportForbidden",
            message: "You can only export your own account data.",
            source: "ExportUserDataHandler");
}
