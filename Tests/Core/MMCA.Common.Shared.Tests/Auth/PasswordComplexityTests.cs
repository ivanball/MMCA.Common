using AwesomeAssertions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Shared.Tests.Auth;

/// <summary>
/// Pins the shared strong-password rule both the server validator and the client form attribute
/// evaluate. The character classes are Unicode-aware: an accented or CJK letter is a letter (never the
/// special character), a non-ASCII uppercase letter is uppercase, and a non-ASCII decimal digit is a
/// digit. Non-ASCII inputs use escapes so the source stays ASCII.
/// </summary>
public sealed class PasswordComplexityTests
{
    [Theory]
    [InlineData("Str0ng!pass")]
    [InlineData("Abcdef1!")]
    [InlineData("Ünicode1!")]
    [InlineData("Passwor１!")]
    [InlineData("密码Abc1!x")]
    public void Evaluate_AcceptsAPasswordMeetingEveryRequirement(string password) =>
        PasswordComplexity.Evaluate(password).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Sh0rt!A")]
    [InlineData("alllower1!")]
    [InlineData("ALLUPPER1!")]
    [InlineData("NoDigits!!")]
    [InlineData("NoSpecial1A")]
    [InlineData("Passwor1é")]
    [InlineData("Abcdef1密码")]
    public void Evaluate_RejectsAPasswordMissingARequirement(string? password) =>
        PasswordComplexity.Evaluate(password).Should().BeFalse();
}
