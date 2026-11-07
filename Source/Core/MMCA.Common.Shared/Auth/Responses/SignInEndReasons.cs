namespace MMCA.Common.Shared.Auth.Responses;

/// <summary>
/// The values <see cref="SignInSummaryResponse.EndReason"/> carries. Each revocation reason matches
/// the refresh session's own reason constant of the same name, so the history reports exactly what
/// the session store recorded; <see cref="Expired"/> is the one reason no revocation writes.
/// </summary>
public static class SignInEndReasons
{
    /// <summary>The user signed out (this device, everywhere, or a password change).</summary>
    public const string SignedOut = "SignedOut";

    /// <summary>A replayed refresh token revoked the account's live sessions (BR-206).</summary>
    public const string ReuseDetected = "ReuseDetected";

    /// <summary>A newer sign-in evicted this one under the per-user session cap.</summary>
    public const string SessionCapExceeded = "SessionCapExceeded";

    /// <summary>The account was deleted and erased.</summary>
    public const string AccountDeleted = "AccountDeleted";

    /// <summary>The sign-in reached its refresh-token expiry without being revoked.</summary>
    public const string Expired = "Expired";
}
