using System.Globalization;

namespace MMCA.Common.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Thrown by <see cref="TenantSaveChangesInterceptor"/> when a save would write across the tenant
/// boundary: inserting, updating or deleting a row owned by a tenant other than the one the current
/// scope resolved, or inserting an untenanted row from a scope that resolved no tenant at all.
/// </summary>
/// <remarks>
/// <para>
/// Isolation on the read side is a query filter, which silently returns nothing when it disagrees
/// with the caller. The write side cannot be silent: a save that has reached the interceptor is
/// about to persist, and the only honest outcomes are "the correct tenant" or "no write". So this
/// throws rather than filtering, and the message names the entity type and both tenants, because
/// the failure is nearly always a scope that was never given a tenant (a background job, a queued
/// handler) rather than a genuine attempt to cross the boundary.
/// </para>
/// <para>
/// It derives from <see cref="InvalidOperationException"/> so existing handler and middleware
/// catch-sites treat it exactly as they treat the framework's other save-time invariant failures.
/// </para>
/// </remarks>
public sealed class CrossTenantWriteException : InvalidOperationException
{
    private const string DefaultMessage =
        "The save would write across the tenant boundary and was rejected.";

    /// <summary>Initializes a new instance with the default message.</summary>
    public CrossTenantWriteException()
        : base(DefaultMessage) { }

    /// <summary>Initializes a new instance with a custom message.</summary>
    /// <param name="message">The exception message.</param>
    public CrossTenantWriteException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance with a custom message and an inner exception.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying failure.</param>
    public CrossTenantWriteException(string message, Exception innerException)
        : base(message, innerException) { }

    private CrossTenantWriteException(
        string message,
        string entityType,
        string? currentTenantId,
        string? entityTenantId)
        : base(message)
    {
        EntityType = entityType;
        CurrentTenantId = currentTenantId;
        EntityTenantId = entityTenantId;
    }

    /// <summary>Gets the CLR type name of the entity the rejected save touched, when known.</summary>
    public string? EntityType { get; }

    /// <summary>
    /// Gets the tenant the saving scope resolved, or <see langword="null"/> when it resolved none.
    /// </summary>
    public string? CurrentTenantId { get; }

    /// <summary>
    /// Gets the tenant recorded on the entity, or <see langword="null"/> when the entity carried
    /// none.
    /// </summary>
    public string? EntityTenantId { get; }

    /// <summary>
    /// Creates the failure for an entity whose recorded tenant differs from the scope's tenant.
    /// </summary>
    /// <param name="entityType">The entity CLR type name.</param>
    /// <param name="operation">The save operation (<c>insert</c>, <c>update</c>, <c>delete</c>).</param>
    /// <param name="currentTenantId">The tenant the scope resolved.</param>
    /// <param name="entityTenantId">The tenant recorded on the entity.</param>
    /// <returns>The exception to throw.</returns>
    internal static CrossTenantWriteException ForMismatch(
        string entityType,
        string operation,
        string currentTenantId,
        string entityTenantId) =>
        new(
            string.Format(
                CultureInfo.InvariantCulture,
                "The {0} of \"{1}\" was rejected: the entity belongs to tenant \"{2}\" but this scope "
                + "resolved tenant \"{3}\". A scope may only write its own tenant's rows.",
                operation,
                entityType,
                entityTenantId,
                currentTenantId),
            entityType,
            currentTenantId,
            entityTenantId);

    /// <summary>
    /// Creates the failure for an insert of an untenanted entity from a scope with no tenant.
    /// </summary>
    /// <param name="entityType">The entity CLR type name.</param>
    /// <returns>The exception to throw.</returns>
    internal static CrossTenantWriteException ForUnresolvedTenant(string entityType) =>
        new(
            string.Format(
                CultureInfo.InvariantCulture,
                "The insert of \"{0}\" was rejected: the entity is tenant-owned, it carries no tenant, "
                + "and this scope resolved none. Resolve a tenant for the scope (ITenantContext.SetTenant) "
                + "or assign the tenant on the entity before saving.",
                entityType),
            entityType,
            currentTenantId: null,
            entityTenantId: null);
}
