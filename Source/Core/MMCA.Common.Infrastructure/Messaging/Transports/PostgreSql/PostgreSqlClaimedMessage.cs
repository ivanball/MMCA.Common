namespace MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

/// <summary>
/// A <c>messages</c> row a receiver claimed: its id and the lease token every settling statement must
/// match (a row whose lease expired and was claimed again carries a different token, so a late settle
/// of the first claim changes nothing), plus the payload.
/// </summary>
/// <param name="Id">The row id.</param>
/// <param name="LockToken">The token of the claim that holds the lease.</param>
/// <param name="MessageId">The event's message id.</param>
/// <param name="Headers">The headers, string to string, keyed ordinally.</param>
/// <param name="Body">The body bytes.</param>
/// <param name="DeliveryCount">How many times the row has been claimed, this claim included.</param>
internal sealed record PostgreSqlClaimedMessage(
    long Id,
    Guid LockToken,
    Guid MessageId,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body,
    int DeliveryCount);
