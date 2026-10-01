using System.Text.RegularExpressions;

namespace MMCA.Common.Shared.Auth;

/// <summary>
/// The one definition of the strong-password rule, shared by the server validator
/// (<c>StrongPasswordRules</c> in MMCA.Common.Application) and the client form attribute
/// (<c>PasswordComplexityAttribute</c> in MMCA.Common.UI), so the two cannot give different verdicts
/// for the same input (rubric section 24, validation parity). It lives in Shared because that is the
/// one project both sides may reference.
/// <para>
/// The character classes are Unicode-aware: an uppercase letter is any <c>\p{Lu}</c>, a lowercase
/// letter any <c>\p{Ll}</c>, a digit any <c>\p{Nd}</c>, and a special character anything that is
/// neither a letter (<c>\p{L}</c>, which includes ideographs) nor a decimal digit. An accented or CJK
/// letter therefore counts as a letter, never as the "special" character.
/// </para>
/// </summary>
public static partial class PasswordComplexity
{
    /// <summary>The minimum password length, in UTF-16 code units.</summary>
    public const int MinimumLength = 8;

    /// <summary>The maximum password length, in UTF-16 code units.</summary>
    public const int MaximumLength = 128;

    /// <summary>Matches when the input contains at least one uppercase letter (<c>\p{Lu}</c>).</summary>
    [GeneratedRegex(@"\p{Lu}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex Uppercase { get; }

    /// <summary>Matches when the input contains at least one lowercase letter (<c>\p{Ll}</c>).</summary>
    [GeneratedRegex(@"\p{Ll}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex Lowercase { get; }

    /// <summary>Matches when the input contains at least one decimal digit (<c>\p{Nd}</c>).</summary>
    [GeneratedRegex(@"\p{Nd}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex Digit { get; }

    /// <summary>
    /// Matches when the input contains at least one character that is neither a letter nor a decimal
    /// digit (<c>[^\p{L}\p{Nd}]</c>).
    /// </summary>
    [GeneratedRegex(@"[^\p{L}\p{Nd}]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex SpecialCharacter { get; }

    /// <summary>
    /// Evaluates the complexity rule: at least <see cref="MinimumLength"/> characters with an
    /// uppercase letter, a lowercase letter, a digit and a special character. The upper bound
    /// (<see cref="MaximumLength"/>) is a separate rule with its own message on both sides.
    /// </summary>
    /// <param name="password">The candidate password.</param>
    /// <returns><see langword="true"/> when every complexity requirement is met.</returns>
    public static bool Evaluate(string? password) =>
        password is { Length: >= MinimumLength }
        && Uppercase.IsMatch(password)
        && Lowercase.IsMatch(password)
        && Digit.IsMatch(password)
        && SpecialCharacter.IsMatch(password);
}
