using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Settings;
using IDbContextFactory = MMCA.Common.Infrastructure.Persistence.DbContexts.Factory.IDbContextFactory;

namespace MMCA.Common.Infrastructure.Persistence.Inbox;

/// <summary>
/// EF-backed <see cref="IInboxStore"/> that records processed messages in the consumer service's
/// own database (the configured outbox data source), so a redelivered message is skipped. Writing
/// the inbox row happens after handlers succeed; combined with the unique index on
/// <see cref="InboxMessage.MessageId"/> this gives at-least-once-with-dedup, so handlers must still
/// be idempotent (a crash between handler commit and inbox write reprocesses once).
/// </summary>
public sealed partial class EfInboxStore(
    IDbContextFactory dbContextFactory,
    IDataSourceResolver dataSourceResolver,
    IOptions<OutboxSettings> outboxOptions,
    ILogger<EfInboxStore> logger) : IInboxStore
{
    /// <inheritdoc />
    public async Task<bool> AlreadyProcessedAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var context = ResolveContext();
        return await context.Set<InboxMessage>()
            .AnyAsync(m => m.MessageId == messageId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkProcessedAsync(Guid messageId, string eventType, CancellationToken cancellationToken)
    {
        var context = ResolveContext();
        context.Set<InboxMessage>().Add(new InboxMessage
        {
            MessageId = messageId,
            EventType = eventType,
            ProcessedOn = DateTime.UtcNow,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent duplicate delivery already inserted this MessageId (unique index).
            // Idempotent — treat as processed.
            LogConcurrentDuplicate(logger, messageId);
        }
    }

    private ApplicationDbContext ResolveContext()
    {
        var target = dataSourceResolver.ResolveLogical(outboxOptions.Value.DataSource, outboxOptions.Value.DatabaseName);
        return dbContextFactory.GetDbContext(target);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Inbox row for message {MessageId} already existed (concurrent duplicate delivery) — treated as processed")]
    private static partial void LogConcurrentDuplicate(ILogger logger, Guid messageId);
}
