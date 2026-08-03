namespace MMCA.Common.API.Idempotency;

/// <summary>
/// Cached snapshot of an idempotent action's response, stored by <see cref="IdempotencyFilter"/>
/// and replayed for duplicate requests with the same idempotency key.
/// </summary>
/// <param name="StatusCode">The HTTP status code of the original response.</param>
/// <param name="ResponseBody">The JSON-serialized response body.</param>
/// <param name="RequestBodyHash">
/// Lowercase hex SHA-256 of the request body that produced the stored response, used to reject a
/// key that is replayed with a different payload. <see langword="null"/> means the record is a
/// legacy entry written before this field existed, in which case the comparison is skipped and the
/// response replays as it always did. The default keeps the field optional so two-property JSON
/// already in the cache still deserializes, which is what makes the rollout safe: entries written
/// by the previous version simply age out of the live 24-hour retention window.
/// </param>
public sealed record IdempotencyRecord(int StatusCode, string ResponseBody, string? RequestBodyHash = null);
