namespace MMCA.Common.Shared.Legal;

/// <summary>
/// Request payload for recording that the signed-in user accepted the Terms of Service.
/// </summary>
/// <remarks>
/// The client echoes the <see cref="LegalAcceptanceDTO.CurrentVersion"/> it showed the user. The
/// server accepts it only when it still equals the configured current version, so a dialog that was
/// left open across a version bump cannot record consent to text the user never saw.
/// </remarks>
/// <param name="Version">The terms version the user agreed to.</param>
public readonly record struct AcceptLegalTermsRequest(string Version);
