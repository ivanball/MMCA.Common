namespace MMCA.Common.Infrastructure.Persistence.Inbox;

/// <summary>
/// Default <see cref="IInboxStore"/> used when the inbox is disabled — never dedups and records
/// nothing, so consumer behavior is exactly as it was before the inbox feature.
/// </summary>
internal sealed class NoOpInboxStore : IInboxStore
{
    public Task<bool> AlreadyProcessedAsync(Guid messageId, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task MarkProcessedAsync(Guid messageId, string eventType, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
