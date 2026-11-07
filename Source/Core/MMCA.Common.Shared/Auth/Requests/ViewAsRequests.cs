namespace MMCA.Common.Shared.Auth.Requests;

/// <summary>
/// Request payload for starting a read-only view-as session on another account (ADR-137).
/// </summary>
/// <param name="Reason">
/// Why the staff user needs to see the account, in free text (10 to 500 characters). Stored on the
/// grant row, which is the audit record of the access.
/// </param>
public sealed record StartViewAsRequest(string? Reason);
