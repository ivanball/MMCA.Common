using System.Linq.Expressions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.Services.Navigation;

/// <summary>
/// Descriptor for a FK reference navigation (e.g. <c>Product.Category</c>).
/// Loads the related entity by matching the parent's nullable FK to the child's primary key.
/// </summary>
/// <typeparam name="TEntity">The parent entity type.</typeparam>
/// <typeparam name="TChild">The related (FK target) entity type.</typeparam>
/// <typeparam name="TChildId">The FK/identifier type (must be a value type for nullable support).</typeparam>
public sealed class FKNavigationDescriptor<TEntity, TChild, TChildId>
    : INavigationDescriptor<TEntity>
    where TChild : AuditableBaseEntity<TChildId>
    where TChildId : struct
{
    /// <inheritdoc />
    public required string PropertyName { get; init; }

    /// <inheritdoc />
    public bool RequiresChildren => false;

    /// <summary>Gets the function that extracts the nullable FK value from each parent.</summary>
    public required Func<TEntity, TChildId?> ParentKeySelector { get; init; }

    /// <summary>Gets the expression selecting the FK property on the child entity.</summary>
    public required Expression<Func<TChild, TChildId>> ChildForeignKeySelector { get; init; }

    /// <summary>Gets the callback to assign loaded children back to each parent.</summary>
    public required Action<TEntity, List<TChild>> AssignAction { get; init; }

    /// <inheritdoc />
    public Task LoadAsync(
        IReadOnlyCollection<TEntity> entities,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
        => NavigationLoader.LoadFKPropertyAsync(
            entities,
            ParentKeySelector,
            ChildForeignKeySelector,
            unitOfWork.GetReadRepository<TChild, TChildId>(),
            AssignAction,
            cancellationToken);
}
