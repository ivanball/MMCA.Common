namespace MMCA.Common.Shared.Legal;

/// <summary>
/// Error codes the legal-acceptance endpoints return, named once so a client that reacts to a
/// specific outcome compares against a constant (the <c>AuthErrorCodes</c> precedent).
/// </summary>
public static class LegalAcceptanceErrorCodes
{
    /// <summary>
    /// An acceptance named a version other than the configured current one (including any acceptance
    /// while no version is configured). A client re-reads the standing and shows the current terms.
    /// </summary>
    public const string VersionNotCurrent = "Legal.VersionNotCurrent";
}
