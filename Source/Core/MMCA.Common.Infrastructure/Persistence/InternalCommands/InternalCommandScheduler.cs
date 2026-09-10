using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.DataSources;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Factory;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Administration;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands;

/// <summary>
/// EF-backed <see cref="IInternalCommandScheduler"/>. Writes one row to the
/// <c>InternalCommands</c> table of the source named by
/// <see cref="InternalCommandsSettings.DataSource"/> and
/// <see cref="InternalCommandsSettings.DatabaseName"/>, on the SAME scoped
/// <see cref="IDbContextFactory"/> the calling handler's repositories use.
/// <para>
/// That shared context is the whole atomicity story. Inside an <c>ITransactional</c> command the
/// factory has already begun a transaction on every context it hands out (and enlists any it creates
/// later), so the row commits with the aggregate change or rolls back with it. Outside a transaction
/// there is nothing to wait for, so the row is saved immediately and the processor is signalled.
/// </para>
/// </summary>
/// <param name="dbContextFactory">Scoped factory whose context the row is written on.</param>
/// <param name="dataSourceResolver">Resolves the configured logical target to a physical source.</param>
/// <param name="options">Bound queue settings naming the target source.</param>
/// <param name="currentUserService">Supplies the principal captured on the row.</param>
/// <param name="tenantContext">Supplies the tenant captured on the row.</param>
/// <param name="correlationContext">Supplies the correlation id captured on the row.</param>
/// <param name="signal">Wakes the processor when a due row was saved outright.</param>
/// <param name="logger">Logger for scheduling diagnostics.</param>
/// <param name="timeProvider">Clock stamping <c>CreatedOn</c> and resolving a relative delay;
/// defaults to <see cref="TimeProvider.System"/> so tests can schedule deterministically.</param>
internal sealed partial class InternalCommandScheduler(
    IDbContextFactory dbContextFactory,
    IDataSourceResolver dataSourceResolver,
    IOptions<InternalCommandsSettings> options,
    ICurrentUserService currentUserService,
    ITenantContext tenantContext,
    ICorrelationContext correlationContext,
    IInternalCommandSignal signal,
    ILogger<InternalCommandScheduler> logger,
    TimeProvider? timeProvider = null) : IInternalCommandScheduler
{
    /// <summary>Column width of <c>UserRoles</c>; a longer list is truncated to fit.</summary>
    internal const int MaxRolesLength = 512;

    private static readonly Error NullCommandError =
        Error.Validation("InternalCommands.NullCommand", "A null command cannot be scheduled.");

    private readonly InternalCommandsSettings _settings = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public Task<Result<Guid>> ScheduleAsync(
        IInternalCommand command,
        TimeSpan delay,
        CancellationToken cancellationToken = default) =>
        ScheduleAsync(
            command,
            delay > TimeSpan.Zero ? _timeProvider.GetUtcNow() + delay : null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<Guid>> ScheduleAsync(
        IInternalCommand command,
        DateTimeOffset? runAt = null,
        CancellationToken cancellationToken = default)
    {
        if (command is null)
        {
            return Result.Failure<Guid>(NullCommandError);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // A past instant is normalized to "now" rather than rejected: a caller computing a deadline
        // that has already slipped wants the work done immediately, not an error to handle.
        var scheduledOn = runAt is { } at && at.UtcDateTime > now ? at.UtcDateTime : now;

        InternalCommandMessage row;
        try
        {
            row = InternalCommandMessage.FromCommand(command, scheduledOn, now, CaptureOrigin());
        }
        catch (NotSupportedException ex)
        {
            // System.Text.Json reports an unserializable payload this way. It is a programming error
            // in the command's shape, but it must not take down the handler that scheduled it: the
            // caller decides whether a command it cannot queue is fatal.
            LogSerializationFailed(logger, command.GetType().FullName ?? command.GetType().Name, ex);
            return Result.Failure<Guid>(SerializationError(command.GetType().Name));
        }

        var context = dbContextFactory.GetDbContext(
            dataSourceResolver.ResolveLogical(_settings.DataSource, _settings.DatabaseName));

#pragma warning disable VSTHRD103 // EF DbSet.Add is intentionally synchronous (in-memory); AddAsync is only for special value generators (EF guidance).
        context.Set<InternalCommandMessage>().Add(row);
#pragma warning restore VSTHRD103

        if (context.Database.CurrentTransaction is not null)
        {
            // Enrolled only. The caller's commit persists the row atomically with its aggregate
            // change, and the processor discovers it on its next poll; signalling now would only buy
            // a query against a transaction that has not committed.
            LogEnrolled(logger, row.Id, row.CommandType, scheduledOn);
            return Result.Success(row.Id);
        }

        // Plain SaveChangesAsync without a user id: InternalCommandMessage is neither auditable nor
        // an aggregate root, so the interceptors have nothing to stamp and no events to capture.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (scheduledOn <= now)
        {
            signal.Signal();
        }

        LogScheduled(logger, row.Id, row.CommandType, scheduledOn);
        return Result.Success(row.Id);
    }

    /// <summary>
    /// Snapshots the principal, tenant and correlation identifiers to restore around the deferred
    /// execution. Roles are flattened to a comma-separated list: the row is read back by a processor
    /// that rebuilds them as claims, and a delimited column keeps the schema free of a child table
    /// for what is almost always one value.
    /// </summary>
    private InternalCommandOrigin CaptureOrigin()
    {
        var activity = Activity.Current;
        string[] roles = [.. currentUserService.Roles];

        return new InternalCommandOrigin(
            currentUserService.UserId,
            roles.Length == 0 ? null : Truncate(string.Join(',', roles), MaxRolesLength),
            tenantContext.TenantId,
            correlationContext.CorrelationId,
            activity?.TraceId.ToString(),
            activity?.SpanId.ToString());
    }

    private static Error SerializationError(string commandType) =>
        Error.Failure(
            "InternalCommands.NotSerializable",
            $"The command '{commandType}' could not be serialized to JSON and was not scheduled.");

    /// <summary>Truncates a value to a column width, preserving null.</summary>
    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(Level = LogLevel.Debug, Message = "Internal command {CommandId} ({CommandType}) enrolled in the caller's transaction for {ScheduledOn:O}")]
    private static partial void LogEnrolled(ILogger logger, Guid commandId, string commandType, DateTime scheduledOn);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Internal command {CommandId} ({CommandType}) scheduled for {ScheduledOn:O}")]
    private static partial void LogScheduled(ILogger logger, Guid commandId, string commandType, DateTime scheduledOn);

    [LoggerMessage(Level = LogLevel.Error, Message = "Internal command {CommandType} could not be serialized and was not scheduled; give it a JSON round-trippable shape")]
    private static partial void LogSerializationFailed(ILogger logger, string commandType, Exception exception);
}
