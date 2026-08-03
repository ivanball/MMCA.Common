namespace MMCA.Common.Shared.Http;

/// <summary>
/// Well-known HTTP header names for the idempotency protocol, shared by the server-side filter
/// that consumes them and the client-side service bases that emit them.
/// </summary>
/// <remarks>
/// Lives in Shared because both ends need the same literal: the API filter
/// (<c>MMCA.Common.API</c>) reads it and the UI service bases (<c>MMCA.Common.UI</c>) write it,
/// and those two packages have no reference to one another. Hard-coding the string in both places
/// is exactly the drift this constant exists to prevent.
/// </remarks>
public static class IdempotencyHeaders
{
    /// <summary>
    /// The header carrying the client-provided idempotency key. A server that has already seen the
    /// key replays the original response instead of executing the action a second time.
    /// </summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>
    /// The response header the server appends when the body it returned came from the idempotency
    /// cache rather than a fresh execution.
    /// </summary>
    public const string IdempotentReplay = "X-Idempotent-Replay";
}
