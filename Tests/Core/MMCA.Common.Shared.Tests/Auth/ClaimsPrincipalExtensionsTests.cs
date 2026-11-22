using System.Security.Claims;
using AwesomeAssertions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Shared.Tests.Auth;

/// <summary>
/// The identity claims every framework reader goes through. The <c>sid</c> reader in particular has
/// to answer null rather than throw for a token that predates the claim, since that is what keeps the
/// claim additive.
/// </summary>
public sealed class ClaimsPrincipalExtensionsTests
{
    [Fact]
    public void FindSessionId_WithTheClaim_ReturnsTheParsedGuid()
    {
        var sessionId = Guid.NewGuid();
        ClaimsPrincipal principal = Principal(new Claim(AuthClaimTypes.SessionId, sessionId.ToString("D")));

        principal.FindSessionId().Should().Be(sessionId);
    }

    [Fact]
    public void FindSessionId_WithoutTheClaim_ReturnsNull()
    {
        ClaimsPrincipal principal = Principal(new Claim(AuthClaimTypes.Subject, "1"));

        principal.FindSessionId().Should().BeNull(
            "a token issued before sid shipped simply has no current-device marker");
    }

    [Fact]
    public void FindSessionId_WithAnUnparsableClaim_ReturnsNull()
    {
        ClaimsPrincipal principal = Principal(new Claim(AuthClaimTypes.SessionId, "not-a-guid"));

        principal.FindSessionId().Should().BeNull("a malformed value must degrade, never throw");
    }

    [Fact]
    public void FindSessionId_OnANullPrincipal_ReturnsNull() =>
        ((ClaimsPrincipal?)null).FindSessionId().Should().BeNull();

    [Fact]
    public void FindSessionId_AcceptsTheUppercaseGuidForm()
    {
        var sessionId = Guid.NewGuid();
        ClaimsPrincipal principal = Principal(
            new Claim(AuthClaimTypes.SessionId, sessionId.ToString("D").ToUpperInvariant()));

        principal.FindSessionId().Should().Be(sessionId);
    }

    [Fact]
    public void FindUserIdValue_ReadsTheRawSubClaim()
    {
        ClaimsPrincipal principal = Principal(new Claim(AuthClaimTypes.Subject, "7"));

        principal.FindUserIdValue().Should().Be("7");
    }

    [Fact]
    public void FindUserIdValue_FallsBackToTheMappedNameIdentifier()
    {
        ClaimsPrincipal principal = Principal(new Claim(ClaimTypes.NameIdentifier, "7"));

        principal.FindUserIdValue().Should().Be(
            "7",
            "the JWT bearer handler maps sub onto NameIdentifier, and readers must not care which shape arrived");
    }

    [Fact]
    public void GetMultiFactorWithheldRoles_ReturnsEveryMarkerInOrder_AndNeverReadsTheRoleClaims()
    {
        ClaimsPrincipal principal = Principal(
            new Claim(ClaimTypes.Role, "Member"),
            new Claim(AuthClaimTypes.MultiFactorWithheldRole, "Admin"),
            new Claim(AuthClaimTypes.MultiFactorWithheldPermission, "orders:manage"),
            new Claim(AuthClaimTypes.MultiFactorWithheldRole, "Auditor"));

        principal.GetMultiFactorWithheldRoles().Should().Equal("Admin", "Auditor");
        principal.HasMultiFactorWithheldRole().Should().BeTrue();
        principal.HasRole("Admin").Should().BeFalse("a withheld role is not held");
    }

    [Fact]
    public void WithheldRoleReaders_WithNoMarker_ReportNone()
    {
        ClaimsPrincipal principal = Principal(
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(AuthClaimTypes.MultiFactorWithheldPermission, "orders:manage"));

        principal.GetMultiFactorWithheldRoles().Should().BeEmpty();
        principal.HasMultiFactorWithheldRole().Should().BeFalse("only a withheld-role marker counts, not a held role or a permission marker");
    }

    [Fact]
    public void WithheldRoleReaders_OnANullPrincipal_ReportNone()
    {
        ClaimsPrincipal? principal = null;

        principal.GetMultiFactorWithheldRoles().Should().BeEmpty();
        principal.HasMultiFactorWithheldRole().Should().BeFalse();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));
}
