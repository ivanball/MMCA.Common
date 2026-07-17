namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// An entity carrying a database-managed optimistic-concurrency token. Implemented by
/// <c>AuditableBaseEntity&lt;TIdentifierType&gt;</c>, so every auditable entity (aggregate roots AND
/// their child entities) exposes its <c>RowVersion</c> through one shape. Exists so the repository's
/// child-entity <c>SetOriginalRowVersion</c> overload can accept any tracked child (e.g. a
/// <c>ProductVariant</c> under a <c>Product</c> aggregate) without a second generic parameter for
/// the child's identifier type (ADR-035: the aggregate-typed overload cannot reach children).
/// </summary>
public interface IRowVersioned
{
    /// <summary>Gets the database-managed optimistic concurrency token (SQL Server rowversion).</summary>
#pragma warning disable CA1819 // byte[] is EF Core's native rowversion shape (mirrors AuditableBaseEntity.RowVersion)
    byte[] RowVersion { get; }
#pragma warning restore CA1819
}
