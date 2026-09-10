using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands;

/// <summary>
/// One unit of deferred work persisted to the <c>InternalCommands</c> table, written in the same
/// transaction as the aggregate change that asked for it. A background processor
/// (<see cref="InternalCommandProcessor"/>) claims it when it comes due and runs it through the
/// normal CQRS pipeline.
/// <para>
/// Deliberately NOT an <c>IAuditableEntity</c>, exactly like <c>OutboxMessage</c>: this is framework
/// bookkeeping, not domain state. It carries no soft-delete flag (so no global query filter applies
/// to it), no audit stamps, and no concurrency token. Its concurrency control is the explicit claim
/// lease below, which is what makes a scaled-out host safe.
/// </para>
/// </summary>
public sealed class InternalCommandMessage
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    /// <summary>
    /// Caches resolved command types per STORED name (an assembly-qualified name, or an
    /// <see cref="InternalCommandNameAttribute"/> identity). Unresolvable names cache as null, so a
    /// dead-lettering row pays the reflection scan once per process rather than once per attempt.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Type?> CommandTypeCache = new(StringComparer.Ordinal);

    /// <summary>Gets the unique identifier for this queued command.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Gets the stored identity of the command, used to resolve its type on deserialization: the
    /// <see cref="InternalCommandNameAttribute"/> name when the command declares one, otherwise its
    /// assembly-qualified type name.
    /// </summary>
    public required string CommandType { get; init; }

    /// <summary>Gets the JSON-serialized command payload.</summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Gets the earliest UTC instant at which this command may run. Equal to
    /// <see cref="CreatedOn"/> for an immediate schedule, later for a deferred one.
    /// </summary>
    public DateTime ScheduledOn { get; init; }

    /// <summary>Gets the UTC instant the row was written.</summary>
    public DateTime CreatedOn { get; init; }

    /// <summary>
    /// Gets or sets the UTC instant the command completed successfully. Null means it has not
    /// completed: still queued, currently claimed, or dead-lettered.
    /// </summary>
    public DateTime? ProcessedOn { get; set; }

    /// <summary>Gets or sets the number of execution attempts made by the processor.</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Gets or sets the failure recorded on the most recent attempt: the handler's first
    /// <c>Result</c> error, or the exception message. Truncated to the column width.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant at which the command was abandoned, having exhausted
    /// <c>InternalCommands:MaxAttempts</c>. A dead-lettered row is never claimed again until an
    /// operator requeues it through <see cref="Application.InternalCommands.IInternalCommandAdministration"/>.
    /// </summary>
    public DateTime? DeadLetteredOn { get; set; }

    /// <summary>
    /// Gets or sets the claim token written together with <see cref="ClaimedUntil"/>. The claiming
    /// replica stamps the outcome only on a row still carrying its own token, so a replica whose
    /// lease expired mid-execution cannot overwrite the record of the replica that took over.
    /// </summary>
    public Guid? ClaimedBy { get; set; }

    /// <summary>
    /// Gets or sets the UTC instant until which this row is claimed by one processor replica. Other
    /// replicas skip rows with an unexpired lease, so a command runs on one replica at a time; if a
    /// replica dies mid-execution, the row becomes claimable again once the lease expires. The same
    /// column carries the retry backoff: a failed attempt re-leases the row for the computed wait.
    /// </summary>
    public DateTime? ClaimedUntil { get; set; }

    /// <summary>
    /// Gets the correlation id of the request that scheduled the command, so the deferred execution
    /// can be tied back to the interaction that asked for it.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>Gets the W3C trace id captured at schedule time, for distributed tracing correlation.</summary>
    public string? TraceId { get; init; }

    /// <summary>Gets the W3C span id captured at schedule time, for distributed tracing correlation.</summary>
    public string? SpanId { get; init; }

    /// <summary>
    /// Gets the id of the user who scheduled the command, restored onto the execution scope's
    /// <c>ICurrentUserService</c> so the authorization decorator and the audit stamps see the same
    /// principal a synchronous execution would have seen. Null for work scheduled by the system.
    /// </summary>
    public UserIdentifierType? UserId { get; init; }

    /// <summary>
    /// Gets the scheduling user's roles, stored as a comma-separated list and restored as role
    /// claims at execution time. Empty or null means the command runs unauthenticated, which is what
    /// an <c>IRequiresPermission</c> command needs to be denied for.
    /// </summary>
    public string? UserRoles { get; init; }

    /// <summary>
    /// Gets the tenant the command was scheduled under, restored onto the execution scope so a
    /// deferred command reads and writes the same tenant's rows the scheduling request did.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Creates a row from a command, serializing it as JSON under its stored identity.
    /// </summary>
    /// <param name="command">The command to persist.</param>
    /// <param name="scheduledOn">The earliest UTC instant the command may run.</param>
    /// <param name="createdOn">The UTC instant the row is written.</param>
    /// <param name="context">The scheduling principal, tenant and correlation identifiers to capture.</param>
    /// <returns>A new row ready for persistence.</returns>
    internal static InternalCommandMessage FromCommand(
        IInternalCommand command,
        DateTime scheduledOn,
        DateTime createdOn,
        InternalCommandOrigin context)
    {
        ArgumentNullException.ThrowIfNull(command);

        var type = command.GetType();
        return new InternalCommandMessage
        {
            CommandType = InternalCommandNameResolver.GetStorageName(type),
            Payload = JsonSerializer.Serialize(command, type, SerializerOptions),
            ScheduledOn = scheduledOn,
            CreatedOn = createdOn,
            CorrelationId = context.CorrelationId,
            TraceId = context.TraceId,
            SpanId = context.SpanId,
            UserId = context.UserId,
            UserRoles = context.UserRoles,
            TenantId = context.TenantId,
        };
    }

    /// <summary>
    /// Deserializes the stored payload back into a command instance.
    /// </summary>
    /// <remarks>
    /// The type is resolved from the stored <see cref="CommandType"/> alone. A command that declares
    /// <see cref="InternalCommandNameAttribute"/> stores that name, so a rename, namespace move, or
    /// assembly move leaves every row still resolvable; a command without one stores its
    /// assembly-qualified name and is resolvable for as long as that name holds.
    /// </remarks>
    /// <returns>The deserialized command, or <see langword="null"/> if the type cannot be resolved.</returns>
    internal IInternalCommand? DeserializeCommand()
    {
        var type = ResolveCommandType();
        if (type is null)
            return null;

        return JsonSerializer.Deserialize(Payload, type, SerializerOptions) as IInternalCommand;
    }

    /// <summary>
    /// Resolves the stored <see cref="CommandType"/> to a CLR type: as a CLR name first, then as an
    /// <see cref="InternalCommandNameAttribute"/> identity. The result, including a failure, caches
    /// under the stored name.
    /// </summary>
    /// <returns>The resolved type, or <see langword="null"/> when the stored name matches nothing.</returns>
    private Type? ResolveCommandType() =>
        // Order is load-bearing, exactly as in OutboxMessage: Type.GetType stays first so a row
        // storing an assembly-qualified name resolves by a direct lookup, and the attribute scan
        // only runs for a stored name that is not a CLR name.
        CommandTypeCache.GetOrAdd(
            CommandType,
            static typeName => Type.GetType(typeName) ?? InternalCommandNameResolver.FindTypeByDeclaredName(typeName));
}
