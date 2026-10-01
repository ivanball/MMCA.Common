using System.ComponentModel.DataAnnotations;
using MMCA.Common.Application.Validation;
using MMCA.Common.UI.Pages.Auth;

namespace MMCA.Common.Architecture.Tests.Ui;

/// <summary>
/// Client/server validation parity for the password rule (rubric section 24): the Register form's
/// <see cref="PasswordComplexityAttribute"/> must give the same verdict as the server's
/// <see cref="StrongPasswordRules{T}"/>, evaluated the way the API's validators evaluate it (as a
/// FluentValidation validator over a model). A disagreement means the form either rejects a password
/// the API would accept or lets through one the API then refuses after a round trip.
/// <para>
/// This lives in the architecture test project because it is the one project that references both
/// sides: the UI test project references MMCA.Common.UI only, and UI may depend on Shared alone.
/// Non-ASCII rows use escapes so the source stays ASCII: <c>\u00E9</c> is a lowercase e with acute,
/// <c>\u00DC</c> an uppercase U with diaeresis, <c>\u5BC6\u7801</c> two CJK ideographs, and
/// <c>\uFF11</c> a full-width digit one.
/// </para>
/// </summary>
public sealed class PasswordRuleParityTests
{
    [Theory]
    [InlineData("Str0ng!pass", "ASCII valid")]
    [InlineData("Abcdef1!", "ASCII valid at the minimum length")]
    [InlineData("alllower1!", "ASCII, no uppercase")]
    [InlineData("ALLUPPER1!", "ASCII, no lowercase")]
    [InlineData("NoDigits!!", "ASCII, no digit")]
    [InlineData("NoSpecial1A", "ASCII, no special character")]
    [InlineData("Sh0rt!A", "ASCII, seven characters")]
    [InlineData("Passwor1\u00E9", "accented lowercase Latin letter as the only special character")]
    [InlineData("\u00DCnicode1!", "uppercase umlaut letter as the only uppercase")]
    [InlineData("Abcdef1\u5BC6\u7801", "CJK characters as the only non-ASCII-alphanumeric characters")]
    [InlineData("\u5BC6\u7801Abc1!x", "CJK characters next to a full ASCII complement")]
    [InlineData("Passwor\uFF11!", "full-width digit as the only digit")]
    public void ClientPasswordComplexity_MatchesServerStrongPasswordRules(string password, string scenario)
    {
        var probe = new PasswordProbe(password);

        // Evaluated the way the EditForm evaluates it: with a validation context naming the member.
        var clientVerdict = new PasswordComplexityAttribute()
            .GetValidationResult(password, new ValidationContext(probe) { MemberName = nameof(PasswordProbe.Password) })
            == ValidationResult.Success;
        var serverVerdict = new StrongPasswordRules<PasswordProbe>(static p => p.Password)
            .Validate(probe)
            .IsValid;

        clientVerdict.Should().Be(
            serverVerdict,
            $"the Register form and the API must agree on '{scenario}' (server says {(serverVerdict ? "valid" : "invalid")})");
    }

    /// <summary>The smallest model the server rule can be bound to.</summary>
    private sealed record PasswordProbe(string Password);
}
