using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// Marks an entity that holds personal or otherwise regulated data and can be irreversibly
/// anonymized to satisfy a data-subject erasure request (GDPR right-to-be-forgotten / CCPA
/// deletion) while keeping the row for referential integrity and audit history.
/// </summary>
/// <remarks>
/// Soft-delete (<see cref="IAuditableEntity.IsDeleted"/>) hides a row from queries but retains its
/// personal data, so it does not by itself satisfy an erasure request. Implement
/// <see cref="IAnonymizable"/> on aggregates that store personal data; an application-layer erasure
/// handler then loads the aggregate, calls <see cref="Anonymize"/>, and saves — anonymizing in
/// place rather than hard-deleting, so foreign keys and the audit trail survive.
/// <para>
/// For personal fields that must remain retrievable, persist them through the AES-256-GCM
/// <c>EncryptedStringConverter</c>; fields that need not survive erasure should be overwritten with
/// non-identifying placeholders inside <see cref="Anonymize"/>. See ADR-005.
/// </para>
/// </remarks>
public interface IAnonymizable
{
    /// <summary>
    /// Irreversibly overwrites this entity's personal data with non-identifying values.
    /// Implementations MUST be idempotent: calling <see cref="Anonymize"/> on an already-anonymized
    /// entity is a no-op that returns success.
    /// </summary>
    /// <returns>A success result, or a failure describing why anonymization could not be applied.</returns>
    Result Anonymize();
}
