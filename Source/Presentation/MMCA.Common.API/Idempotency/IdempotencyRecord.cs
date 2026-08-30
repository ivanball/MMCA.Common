namespace MMCA.Common.API.Idempotency;

/// <summary>
/// Cached snapshot of an idempotent action's response, stored by <see cref="IdempotencyFilter"/>
/// and replayed for duplicate requests with the same idempotency key.
/// </summary>
/// <param name="StatusCode">The HTTP status code of the original response.</param>
/// <param name="ResponseBody">The JSON-serialized response body.</param>
/// <param name="RequestBodyHash">
/// Lowercase hex SHA-256 of the request body that produced the stored response. Every record
/// carries one, so a key replayed with a different payload is always rejected rather than served
/// someone else's response. A body-less request hashes the empty payload.
/// </param>
public sealed record IdempotencyRecord(int StatusCode, string ResponseBody, string RequestBodyHash);
