using System.Linq.Expressions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.Services.Navigation;

/// <summary>
/// Descriptor for a child collection navigation (e.g. <c>Event.Rooms</c>).
/// Loads children whose FK matches the parent's primary key.
/// </summary>
/// <typeparam name="TEntity">The parent entity type.</typeparam>
/// <typeparam name="TParentId">The parent's primary key type.</typeparam>
/// <typeparam name="TChild">The child entity type.</typeparam>
/// <typeparam name="TChildId">The child entity's primary key type.</typeparam>
public sealed class ChildNavigationDescriptor<TEntity, TParentId, TChild, TChildId>
    : INavigationDescriptor<TEntity>
    where TParentId : notnull
    where TChild : AuditableBaseEntity<TChildId>
    where TChildId : notnull
{
    /// <inheritdoc />
    public required string PropertyName { get; init; }

    /// <inheritdoc />
    public bool RequiresChildren => true;

    /// <summary>Gets the function that extracts the parent's primary key.</summary>
    public required Func<TEntity, TParentId> ParentKeySelector { get; init; }

    /// <summary>Gets the expression selecting the parent FK on the child entity.</summary>
    public required Expression<Func<TChild, TParentId>> ChildForeignKeySelector { get; init; }

    /// <summary>Gets the callback to assign loaded children back to each parent.</summary>
    public required Action<TEntity, List<TChild>> AssignAction { get; init; }

    /// <inheritdoc />
    public Task LoadAsync(
        IReadOnlyCollection<TEntity> entities,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
        => NavigationLoader.LoadChildrenPropertyAsync(
            entities,
            ParentKeySelector,
            ChildForeignKeySelector,
            unitOfWork.GetReadRepository<TChild, TChildId>(),
            AssignAction,
            cancellationToken);
}
