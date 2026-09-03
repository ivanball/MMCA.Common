using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Application.Interfaces.Mapping;

/// <summary>
/// Opt-in server-side projection from an entity queryable straight to a DTO queryable, so the
/// database returns only the columns the DTO actually has.
/// <para>
/// It is the pushdown counterpart of <see cref="IEntityDTOMapper{TEntity, TEntityDTO, TIdentifierType}"/>:
/// the mapper maps rows AFTER they are materialized, so the query must select whole entities (every
/// column, plus a JOIN per include the DTO happens to flatten); a projector rewrites the query so the
/// provider selects the DTO's columns directly. Registering one for an entity is what switches the
/// query service's list reads onto the projected path. Nothing breaks when none is registered: the
/// service falls back to materialize-then-map.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// Implementations are typically a Mapperly <c>[Mapper]</c>-generated static projection wrapped in a
/// small class, for example:
/// </para>
/// <code>
/// [Mapper]
/// internal static partial class OrderDTOProjection
/// {
///     internal static partial IQueryable&lt;OrderDTO&gt; ProjectToDTO(IQueryable&lt;Order&gt; source);
/// }
///
/// public sealed class OrderDTOProjector : IEntityDTOProjector&lt;Order, OrderDTO, int&gt;
/// {
///     public IQueryable&lt;OrderDTO&gt; ProjectTo(IQueryable&lt;Order&gt; source) =&gt;
///         OrderDTOProjection.ProjectToDTO(source);
/// }
/// </code>
/// <para>
/// A projection is an expression tree the provider must translate, which constrains what it can
/// express: no instance sub-mappers, no custom mapping methods (<c>Use = nameof(...)</c>), no
/// after-map hooks, nothing that would have to run in .NET on a materialized object. A DTO whose
/// shape needs any of those simply does not get a projector, and its reads keep using the mapper.
/// </para>
/// <para>
/// A projector MUST produce the same values as the entity's mapper for the same row. The two paths
/// are chosen by configuration, so a divergence would make a response depend on whether a projector
/// happened to be registered. Pin the equivalence with a test.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The domain entity type.</typeparam>
/// <typeparam name="TEntityDTO">The DTO type.</typeparam>
/// <typeparam name="TIdentifierType">The entity's primary key type.</typeparam>
public interface IEntityDTOProjector<TEntity, TEntityDTO, TIdentifierType>
    where TEntity : AuditableBaseEntity<TIdentifierType>
    where TEntityDTO : IBaseDTO<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <summary>
    /// Rewrites an entity queryable into a DTO queryable. The result must still be a translatable
    /// queryable: do not materialize inside the implementation.
    /// </summary>
    /// <param name="source">The entity queryable, already filtered, sorted, and paged.</param>
    /// <returns>The projected DTO queryable.</returns>
    IQueryable<TEntityDTO> ProjectTo(IQueryable<TEntity> source);
}
