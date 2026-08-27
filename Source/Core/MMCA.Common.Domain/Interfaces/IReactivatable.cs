using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Interfaces;

/// <summary>
/// Opt-in contract for a soft-deletable entity that may be brought back into the visible set
/// (BR-135). <c>AuditableBaseEntity.Undelete()</c> is deliberately non-public, so reversing a
/// soft delete is a decision each entity makes for itself; implementing this interface is how an
/// entity publishes that decision, typically as
/// <c>public Result Reactivate() =&gt; Undelete();</c>.
/// <para>
/// The aggregate helper
/// <c>AuditableAggregateRootEntity{TIdentifierType}.RestoreChild{TChild, TChildId}</c> constrains
/// its child to this interface: a child that does not implement it cannot be restored through the
/// helper, which is the point. Resurrection is a business decision per entity, not a capability the
/// base class hands out to every soft-deletable row.
/// </para>
/// </summary>
public interface IReactivatable
{
    /// <summary>
    /// Reverses this entity's soft delete, returning it to the active set.
    /// </summary>
    /// <returns>A success result, or a failure when the entity is not deleted.</returns>
    Result Reactivate();
}
