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
#pragma warning disable VSTHRD103 // EF DbSet.Add is intentionally synchronous (in-memory); AddAsync is only for special value generators (EF guidance).
        var entry = context.Set<InboxMessage>().Add(new InboxMessage
        {
            MessageId = messageId,
            EventType = eventType,
            ProcessedOn = DateTime.UtcNow,
        });
#pragma warning restore VSTHRD103

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The context is cached per data source for the whole scope, so the rejected row must be
            // detached: left Added, every later SaveChangesAsync on this scope would re-attempt the
            // failed insert and fail. Same idiom as DomainEventSaveChangesInterceptor uses when it
            // discards an abandoned capture.
            entry.State = EntityState.Detached;

            // Only a concurrent duplicate delivery (the unique index on MessageId) is idempotent and
            // safe to absorb. Re-query instead of sniffing provider-specific error codes, so the check
            // holds for SQL Server and SQLite alike. Any other write failure must surface: swallowing
            // it would ACK a message whose inbox row was never written, hiding the failure from the
            // broker's redelivery.
            if (!await AlreadyProcessedAsync(messageId, cancellationToken).ConfigureAwait(false))
                throw;

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
