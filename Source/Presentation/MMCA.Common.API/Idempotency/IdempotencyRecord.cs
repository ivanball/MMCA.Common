namespace MMCA.Common.API.Idempotency;

/// <summary>
/// Cached snapshot of an idempotent action's response, stored by <see cref="IdempotencyFilter"/>
/// and replayed for duplicate requests with the same idempotency key.
/// </summary>
/// <param name="StatusCode">The HTTP status code of the original response.</param>
/// <param name="ResponseBody">The JSON-serialized response body.</param>
public sealed record IdempotencyRecord(int StatusCode, string ResponseBody);
