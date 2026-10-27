using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace MMCA.Common.Shared.ValueObjects.Contact;

/// <summary>
/// The United States postal regions by their USPS codes: the 50 states, the District of Columbia,
/// the five inhabited territories (AS, GU, MP, PR, VI) and the three military (APO/FPO/DPO) regions
/// (AA, AE, AP). Lives in Shared so a UI's state picker and the server's validation
/// (<c>UsAddressValidator</c>) accept exactly the same codes.
/// </summary>
public static partial class UsPostalRegions
{
    /// <summary>Every region in display order: the states and DC, then the territories, then the military regions.</summary>
    public static IReadOnlyList<UsPostalRegion> All { get; } =
    [
        new("AL", "Alabama"), new("AK", "Alaska"), new("AZ", "Arizona"), new("AR", "Arkansas"), new("CA", "California"),
        new("CO", "Colorado"), new("CT", "Connecticut"), new("DE", "Delaware"), new("DC", "District of Columbia"),
        new("FL", "Florida"), new("GA", "Georgia"), new("HI", "Hawaii"), new("ID", "Idaho"), new("IL", "Illinois"),
        new("IN", "Indiana"), new("IA", "Iowa"), new("KS", "Kansas"), new("KY", "Kentucky"), new("LA", "Louisiana"),
        new("ME", "Maine"), new("MD", "Maryland"), new("MA", "Massachusetts"), new("MI", "Michigan"), new("MN", "Minnesota"),
        new("MS", "Mississippi"), new("MO", "Missouri"), new("MT", "Montana"), new("NE", "Nebraska"), new("NV", "Nevada"),
        new("NH", "New Hampshire"), new("NJ", "New Jersey"), new("NM", "New Mexico"), new("NY", "New York"),
        new("NC", "North Carolina"), new("ND", "North Dakota"), new("OH", "Ohio"), new("OK", "Oklahoma"), new("OR", "Oregon"),
        new("PA", "Pennsylvania"), new("RI", "Rhode Island"), new("SC", "South Carolina"), new("SD", "South Dakota"),
        new("TN", "Tennessee"), new("TX", "Texas"), new("UT", "Utah"), new("VT", "Vermont"), new("VA", "Virginia"),
        new("WA", "Washington"), new("WV", "West Virginia"), new("WI", "Wisconsin"), new("WY", "Wyoming"),
        new("AS", "American Samoa"), new("GU", "Guam"), new("MP", "Northern Mariana Islands"), new("PR", "Puerto Rico"),
        new("VI", "U.S. Virgin Islands"),
        new("AA", "Armed Forces Americas"), new("AE", "Armed Forces Europe, Middle East, Africa and Canada"),
        new("AP", "Armed Forces Pacific"),
    ];

    private static readonly FrozenSet<string> Codes =
        All.Select(region => region.Code).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> CodesByName =
        All.ToFrozenDictionary(region => region.Name, region => region.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="code"/> is one of the USPS region codes (case-insensitive, surrounding spaces ignored).</summary>
    /// <param name="code">The code to check.</param>
    /// <returns><see langword="true"/> for a known region code.</returns>
    public static bool IsKnownCode(string? code) => code is not null && Codes.Contains(code.Trim());

    /// <summary>
    /// The USPS code for <paramref name="value"/>, which may already be a code or may be the region's
    /// full name ("Georgia"), as an address saved before a picker existed might hold.
    /// </summary>
    /// <param name="value">A code or a name.</param>
    /// <returns>The upper-case code, or <see langword="null"/> when the value is neither.</returns>
    public static string? ToCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (Codes.Contains(trimmed))
            return trimmed.ToUpperInvariant();

        return CodesByName.TryGetValue(trimmed, out var code) ? code : null;
    }

    /// <summary>Whether <paramref name="zipCode"/> is a 5-digit ZIP code or a ZIP+4 ("12345" or "12345-6789").</summary>
    /// <param name="zipCode">The ZIP code to check (surrounding spaces ignored).</param>
    /// <returns><see langword="true"/> when it is well formed.</returns>
    public static bool IsValidZipCode(string? zipCode) =>
        zipCode is not null && ZipCodePattern.IsMatch(zipCode.Trim());

    [GeneratedRegex(@"^\d{5}(-\d{4})?$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 100)]
    private static partial Regex ZipCodePattern { get; }
}

/// <summary>One United States postal region.</summary>
/// <param name="Code">The two-letter USPS code ("GA").</param>
/// <param name="Name">The region's name ("Georgia").</param>
public sealed record UsPostalRegion(string Code, string Name);
