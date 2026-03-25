using System.Linq.Expressions;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Domain.Specifications;

/// <summary>
/// Base class for the Specification pattern. Specifications serve dual purposes:
/// <list type="bullet">
///   <item><see cref="Criteria"/> is an expression tree usable in LINQ-to-DB (EF Core translates it to SQL).</item>
///   <item><see cref="IsSatisfiedBy"/> compiles the expression for in-memory evaluation.</item>
/// </list>
/// </summary>
/// <typeparam name="TEntity">The entity type this specification applies to.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
public abstract class Specification<TEntity, TIdentifierType>
    : ISpecification<TEntity, TIdentifierType>
    where TEntity : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    protected Specification() { }

    /// <inheritdoc />
    public abstract Expression<Func<TEntity, bool>> Criteria { get; }

    // Lazy-compiled delegate cached for in-memory evaluation.
    // Avoids recompiling the expression tree on every IsSatisfiedBy call.
    private Func<TEntity, bool>? _compiled;

    /// <inheritdoc />
    public virtual bool IsSatisfiedBy(TEntity entity)
    {
        _compiled ??= Criteria.Compile();
        return _compiled(entity);
    }
}

/// <summary>
/// Composes two specifications with a logical AND. Uses <c>Expression.Invoke</c>
/// to embed each specification's expression tree into a new lambda, preserving
/// EF Core translatability for LINQ-to-DB queries.
/// </summary>
/// <typeparam name="TEntity">The entity type this specification applies to.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
public sealed class AndSpecification<TEntity, TIdentifierType>(
    ISpecification<TEntity, TIdentifierType> spec1,
    ISpecification<TEntity, TIdentifierType> spec2)
    : Specification<TEntity, TIdentifierType>()
    where TEntity : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public override Expression<Func<TEntity, bool>> Criteria
    {
        get
        {
            var parameter = Expression.Parameter(typeof(TEntity), "entity");
            var body = Expression.AndAlso(
                Expression.Invoke(spec1.Criteria, parameter),
                Expression.Invoke(spec2.Criteria, parameter));
            return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
        }
    }
}

/// <summary>
/// Composes two specifications with a logical OR using <c>Expression.Invoke</c> composition.
/// </summary>
/// <typeparam name="TEntity">The entity type this specification applies to.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
public sealed class OrSpecification<TEntity, TIdentifierType>(
    ISpecification<TEntity, TIdentifierType> spec1,
    ISpecification<TEntity, TIdentifierType> spec2)
    : Specification<TEntity, TIdentifierType>
    where TEntity : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public override Expression<Func<TEntity, bool>> Criteria
    {
        get
        {
            var parameter = Expression.Parameter(typeof(TEntity), "entity");
            var body = Expression.OrElse(
                Expression.Invoke(spec1.Criteria, parameter),
                Expression.Invoke(spec2.Criteria, parameter));
            return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
        }
    }
}

/// <summary>
/// Negates a specification using <c>Expression.Not</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type this specification applies to.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
public sealed class NotSpecification<TEntity, TIdentifierType>(
    ISpecification<TEntity, TIdentifierType> spec)
    : Specification<TEntity, TIdentifierType>
    where TEntity : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public override Expression<Func<TEntity, bool>> Criteria
    {
        get
        {
            var parameter = Expression.Parameter(typeof(TEntity), "entity");
            var body = Expression.Not(
                Expression.Invoke(spec.Criteria, parameter));
            return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
        }
    }
}
