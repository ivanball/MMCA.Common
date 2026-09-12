namespace MMCA.Common.Shared.Messaging;

/// <summary>
/// Well-known transport header names carrying the ambient request context across a broker hop:
/// who raised the event, which tenant they were acting in, and which interaction it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Lives in Shared for the same reason <see cref="Http.IdempotencyHeaders"/> does: both ends need
/// the same literal and neither end may reference the other. The publisher side is
/// <c>BrokerMessageBus</c> (MMCA.Common.Infrastructure) and the consumer side is
/// <c>IntegrationEventConsumer</c>, which today share an assembly but are the pair that splits
/// first when a module is extracted into its own service. Hard-coding the strings on both sides is
/// exactly the drift these constants exist to prevent.
/// </para>
/// <para>
/// The values are stable wire contract. Renaming one is a breaking change for any service still
/// running the previous version: a consumer that does not recognize a header simply leaves its
/// defaults in place, so a rename degrades silently rather than loudly.
/// </para>
/// </remarks>
public static class MessageHeaders
{
    /// <summary>The tenant the publishing scope was resolved to, absent for a tenant-less publish.</summary>
    public const string TenantId = "MMCA-Tenant-Id";

    /// <summary>The publishing user's identifier, absent for work raised by the system.</summary>
    public const string UserId = "MMCA-User-Id";

    /// <summary>The publishing user's roles as a comma-separated list, absent when they hold none.</summary>
    public const string UserRoles = "MMCA-User-Roles";

    /// <summary>The correlation id of the interaction that produced the message.</summary>
    public const string CorrelationId = "MMCA-Correlation-Id";
}
