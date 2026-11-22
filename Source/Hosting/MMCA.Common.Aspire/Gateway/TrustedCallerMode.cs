namespace MMCA.Common.Aspire.Gateway;

/// <summary>
/// How the gateway edge limiter treats a request that proves
/// <see cref="GatewayRateLimitingSettings.TrustedCallerSecret"/>
/// (<see cref="GatewayRateLimitingSettings.TrustedCallerMode"/>).
/// </summary>
public enum TrustedCallerMode
{
    /// <summary>
    /// The default. A trusted-caller request that carries the end visitor's address in
    /// <c>X-Forwarded-For</c> is counted against THAT visitor's per-IP window and against every named
    /// route policy keyed on the visitor, exactly as the visitor's own direct request would be. A
    /// trusted-caller request with no visitor address (a background call) takes the no-limiter
    /// partition.
    /// </summary>
    PerVisitor = 0,

    /// <summary>
    /// Every trusted-caller request takes the no-limiter partition on the per-IP window and the
    /// named route policies, whatever visitor address it carries.
    /// </summary>
    Exempt = 1,
}
