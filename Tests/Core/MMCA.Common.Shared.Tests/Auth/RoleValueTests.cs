using AwesomeAssertions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Shared.Tests.Auth;

/// <summary>
/// Pins the documented contract of <see cref="RoleValue.Validate"/>: role comparison is
/// case-insensitive regardless of the comparer the caller's set was built with. A set built with
/// the default ordinal comparer (the natural result of a collection expression or
/// <c>ToHashSet()</c> without a comparer) used to reject a correctly-spelled role that differed
/// only in casing.
/// </summary>
public sealed class RoleValueTests
{
    private const string Source = nameof(RoleValueTests);

    [Fact]
    public void Validate_WithDefaultComparerSet_MatchesRoleCaseInsensitively()
    {
        // A default-comparer set: ordinal, so "admin" is not "Admin" to the set's own lookup.
        var knownRoles = new HashSet<string> { RoleNames.Admin, RoleNames.Customer };

        var result = RoleValue.Validate("admin", knownRoles, Source);

        result.IsSuccess.Should().BeTrue(
            "role comparison is case-insensitive whatever comparer the caller's set carries");
    }

    [Fact]
    public void Validate_WithOrdinalIgnoreCaseSet_MatchesRoleCaseInsensitively()
    {
        var knownRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RoleNames.Admin };

        RoleValue.Validate("ADMIN", knownRoles, Source).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithExactRole_Succeeds()
    {
        var knownRoles = new HashSet<string> { RoleNames.Organizer, RoleNames.Attendee };

        RoleValue.Validate(RoleNames.Organizer, knownRoles, Source).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithUnknownRole_FailsWithInvariantCode()
    {
        var knownRoles = new HashSet<string> { RoleNames.Admin };

        var result = RoleValue.Validate("Wizard", knownRoles, Source);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle();
        result.Errors[0].Code.Should().Be("User.Role.Invalid");
        result.Errors[0].Source.Should().Be(Source);
    }

    [Fact]
    public void Validate_WithNullRole_Fails()
    {
        var knownRoles = new HashSet<string> { RoleNames.Admin };

        RoleValue.Validate(null!, knownRoles, Source).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithNullKnownRoles_Throws()
    {
        var act = () => RoleValue.Validate(RoleNames.Admin, null!, Source);

        act.Should().Throw<ArgumentNullException>();
    }
}
