using System.Security.Claims;
using AwesomeAssertions;
using MMCA.Common.API.Authentication;

namespace MMCA.Common.API.Tests.Authentication;

/// <summary>
/// Verifies <see cref="AppleUserNameClaims.AddFromUserField"/> against Apple's documented callback
/// contract: the <c>user</c> field carries <c>{"name":{"firstName","lastName"},"email"}</c> on the first
/// authorization only, so a present name becomes the given-name and surname claims, while an absent,
/// partial, or malformed field adds nothing and never throws (the sign-in must still complete).
/// </summary>
public sealed class AppleUserNameClaimsTests
{
    private const string Issuer = "Apple";

    private static ClaimsIdentity NewIdentity() =>
        new([new Claim(ClaimTypes.NameIdentifier, "apple-subject")], "Apple");

    [Fact]
    public void AddFromUserField_FirstSignInPayload_AddsGivenNameAndSurname()
    {
        var identity = NewIdentity();

        AppleUserNameClaims.AddFromUserField(
            identity,
            """{"name":{"firstName":"Jane","lastName":"Appleseed"},"email":"jane@example.com"}""",
            Issuer);

        identity.FindFirst(ClaimTypes.GivenName)?.Value.Should().Be("Jane");
        identity.FindFirst(ClaimTypes.Surname)?.Value.Should().Be("Appleseed");
        identity.FindFirst(ClaimTypes.GivenName)?.Issuer.Should().Be(Issuer);
    }

    [Fact]
    public void AddFromUserField_NamesWithSurroundingWhitespace_AreTrimmed()
    {
        var identity = NewIdentity();

        AppleUserNameClaims.AddFromUserField(
            identity, """{"name":{"firstName":"  Jane ","lastName":" Appleseed  "}}""", Issuer);

        identity.FindFirst(ClaimTypes.GivenName)?.Value.Should().Be("Jane");
        identity.FindFirst(ClaimTypes.Surname)?.Value.Should().Be("Appleseed");
    }

    [Fact]
    public void AddFromUserField_OnlyFirstNameShared_AddsGivenNameOnly()
    {
        var identity = NewIdentity();

        AppleUserNameClaims.AddFromUserField(identity, """{"name":{"firstName":"Jane","lastName":""}}""", Issuer);

        identity.FindFirst(ClaimTypes.GivenName)?.Value.Should().Be("Jane");
        identity.HasClaim(c => c.Type == ClaimTypes.Surname).Should().BeFalse(
            "a blank last name must leave the surname to the controller's placeholder");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddFromUserField_LaterSignInWithNoUserField_AddsNothing(string? userJson)
    {
        var identity = NewIdentity();

        AppleUserNameClaims.AddFromUserField(identity, userJson, Issuer);

        identity.Claims.Should().ContainSingle("Apple sends the user field on the first authorization only");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"name\":")]
    [InlineData("[]")]
    [InlineData("\"Jane\"")]
    [InlineData("""{"email":"jane@example.com"}""")]
    [InlineData("""{"name":"Jane Appleseed"}""")]
    [InlineData("""{"name":{"firstName":42,"lastName":true}}""")]
    public void AddFromUserField_MalformedOrUnexpectedShape_AddsNothingAndDoesNotThrow(string userJson)
    {
        var identity = NewIdentity();

        var act = () => AppleUserNameClaims.AddFromUserField(identity, userJson, Issuer);

        act.Should().NotThrow("a bad name payload must never fail the sign-in");
        identity.Claims.Should().ContainSingle();
    }

    [Fact]
    public void AddFromUserField_ClaimsAlreadyPresent_AreLeftUntouched()
    {
        var identity = NewIdentity();
        identity.AddClaim(new Claim(ClaimTypes.GivenName, "Existing"));
        identity.AddClaim(new Claim(ClaimTypes.Surname, "Claims"));

        AppleUserNameClaims.AddFromUserField(
            identity, """{"name":{"firstName":"Jane","lastName":"Appleseed"}}""", Issuer);

        identity.FindAll(ClaimTypes.GivenName).Should().ContainSingle().Which.Value.Should().Be("Existing");
        identity.FindAll(ClaimTypes.Surname).Should().ContainSingle().Which.Value.Should().Be("Claims");
    }
}
