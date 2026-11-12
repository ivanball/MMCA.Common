using System.Text.Json;
using System.Text.Json.Serialization;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.UI.Services.Offline;

/// <summary>
/// One write the <see cref="IOfflineWriteQueue"/> holds until it can be replayed: what kind of write
/// it is (the key its <see cref="IOfflineWriteHandler"/> answers to), its payload as JSON, the
/// idempotency key it carries from enqueue to replay, and the row version it was based on.
/// </summary>
public sealed record PendingWrite
{
    /// <summary>Gets the entry's id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the handler key, for example <c>Engagement.Bookmark.Create.v1</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the idempotency key the replay sends, so a write the server already ran is not run twice.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Gets the row version the write was based on, or <see langword="null"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "The EF rowversion token in its wire shape (byte[]), the same exemption the repo grants DTO, Request and Command files; this record is persisted to the local store, not a domain type.")]
    public byte[]? BasedOnRowVersion { get; init; }

    /// <summary>Gets the payload, serialized with <see cref="JsonSerializerOptions.Web"/>.</summary>
    public required string PayloadJson { get; init; }

    /// <summary>Gets the key that pairs writes that cancel each other out (see <see cref="IOfflineWriteQueue.CancelPendingAsync"/>).</summary>
    public string? CoalesceKey { get; init; }

    /// <summary>Gets the signed-in user's id when the write was queued, or <see langword="null"/> for an anonymous user.</summary>
    public string? UserScope { get; init; }

    /// <summary>Gets when the write was queued.</summary>
    public required DateTimeOffset EnqueuedOn { get; init; }

    /// <summary>Gets how many replays failed transiently so far.</summary>
    public int Attempts { get; init; }

    /// <summary>
    /// Gets the <c>If-Match</c> value for the replay: the weak ETag of <see cref="BasedOnRowVersion"/>
    /// (<see cref="ConcurrencyETag.Format"/>), or <see langword="null"/> when no row version was given.
    /// </summary>
    [JsonIgnore]
    public string? IfMatch => BasedOnRowVersion is null ? null : ConcurrencyETag.Format(BasedOnRowVersion);

    /// <summary>Deserializes <see cref="PayloadJson"/> with <see cref="JsonSerializerOptions.Web"/>.</summary>
    /// <typeparam name="TPayload">The payload type the write was queued with.</typeparam>
    /// <returns>The payload, or <see langword="null"/> when the JSON is the literal <see langword="null"/>.</returns>
    public TPayload? ReadPayload<TPayload>() => JsonSerializer.Deserialize<TPayload>(PayloadJson, JsonSerializerOptions.Web);
}
