namespace MMCA.Common.Application.Auth.Legal;

/// <summary>
/// Server-side configuration for the opt-in Terms of Service acceptance. Bound from the
/// <c>Legal</c> configuration section by <c>AddLegalAcceptance(configuration)</c>.
/// </summary>
/// <remarks>
/// <b>Unset is the off switch.</b> With no <see cref="CurrentTermsVersion"/> the registration flow
/// does not ask for acceptance, the read endpoint reports every user as current, and the accept
/// endpoint has nothing to accept, so a host that never configures it sees no change at all.
/// </remarks>
public sealed class LegalAcceptanceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Legal";

    /// <summary>
    /// The terms version every user must have accepted (for example <c>2026-10-01</c>). Changing it
    /// asks every signed-in user to accept again. Null or whitespace turns the feature off.
    /// </summary>
    public string? CurrentTermsVersion { get; init; }
}
