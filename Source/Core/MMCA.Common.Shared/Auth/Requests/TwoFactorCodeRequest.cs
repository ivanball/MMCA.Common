namespace MMCA.Common.Shared.Auth.Requests;

/// <summary>
/// Request payload for every two-factor action that has to be proved with a live code: confirming
/// enrollment, disabling the second factor, and regenerating the recovery codes.
/// </summary>
/// <remarks>
/// One payload for the three actions rather than three identical ones, because the code is the whole
/// body in each case and the account is taken from the authenticated caller, never from the request.
/// Disable and regenerate demand a code for the same reason enrollment does: whoever holds a stolen
/// access token must not be able to strip the account's second factor with it.
/// </remarks>
/// <param name="Code">
/// The code from the authenticator app, or one of the account's single-use recovery codes.
/// </param>
public readonly record struct TwoFactorCodeRequest(string Code);
