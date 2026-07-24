using System.Linq.Expressions;

namespace MMCA.Common.Application.Interfaces.Infrastructure;

/// <summary>
/// Persistence-agnostic builder for the SET clause of a bulk update
/// (<see cref="IWriteRepository{TEntity,TIdentifierType}.ExecuteUpdateAsync"/>).
/// Mirrors the shape of EF Core's <c>SetPropertyCalls</c> without leaking EF Core into the
/// Application layer: handlers describe WHICH properties change and to WHAT, and the
/// Infrastructure implementation translates the calls into the provider's set-based UPDATE.
/// </summary>
/// <typeparam name="TEntity">The entity type being updated.</typeparam>
public interface IUpdatePropertySetter<TEntity>
{
    /// <summary>Sets a property to a fixed value (e.g. a status, a timestamp).</summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="property">The property to set.</param>
    /// <param name="value">The value to assign.</param>
    /// <returns>The same builder, for chaining.</returns>
    IUpdatePropertySetter<TEntity> Set<TProperty>(
        Expression<Func<TEntity, TProperty>> property,
        TProperty value);

    /// <summary>
    /// Sets a property from an expression over the CURRENT database row (e.g.
    /// <c>quantity => quantity.Amount - 5</c>), enabling atomic read-modify-write updates
    /// such as conditional counter decrements where the database itself arbitrates races.
    /// </summary>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <param name="property">The property to set.</param>
    /// <param name="valueFactory">An expression computing the new value from the current row.</param>
    /// <returns>The same builder, for chaining.</returns>
    IUpdatePropertySetter<TEntity> Set<TProperty>(
        Expression<Func<TEntity, TProperty>> property,
        Expression<Func<TEntity, TProperty>> valueFactory);
}
