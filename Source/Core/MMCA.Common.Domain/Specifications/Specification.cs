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
/// A specification built from a pre-composed <see cref="Criteria"/> expression. Useful for
/// dynamically constructed predicates (e.g. the cross-source filter produced by
/// <c>CrossSourceSpecification</c>) where there is no fixed, hand-written specification class.
/// </summary>
/// <typeparam name="TEntity">The entity type this specification applies to.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
/// <param name="criteria">The criteria expression (must be EF-translatable to be used in a DB query).</param>
public sealed class InlineSpecification<TEntity, TIdentifierType>(Expression<Func<TEntity, bool>> criteria)
    : Specification<TEntity, TIdentifierType>
    where TEntity : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    /// <inheritdoc />
    public override Expression<Func<TEntity, bool>> Criteria { get; } =
        criteria ?? throw new ArgumentNullException(nameof(criteria));
}

/// <summary>
/// Composes two specifications with a logical AND.
/// </summary>
/// <remarks>
/// <para>
/// The two criteria are merged by <b>parameter substitution</b> (see <c>ParameterReplacer</c>):
/// the right-hand body is rebound onto the left-hand lambda's parameter and the two bodies are
/// joined with <see cref="Expression.AndAlso(Expression, Expression)"/>. The result is a tree
/// indistinguishable from a hand-written predicate.
/// </para>
/// <para>
/// It deliberately does not use <c>Expression.Invoke</c>. An <see cref="InvocationExpression"/>
/// survives into the query tree, and while EF Core's relational providers can usually unwrap it,
/// others (Cosmos in particular) throw at translation time, so an ANDed specification failed on
/// exactly the engines the framework is meant to be portable across.
/// </para>
/// <para>
/// The composed expression is built once per instance and cached: the previous implementation
/// rebuilt the whole tree on every <see cref="Criteria"/> read, and the query pipeline reads it at
/// least once per request.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity type this specification applies to.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
/// <param name="spec1">The left-hand specification.</param>
/// <param name="spec2">The right-hand specification.</param>
public sealed class AndSpecification<TEntity, TIdentifierType>(
    ISpecification<TEntity, TIdentifierType> spec1,
    ISpecification<TEntity, TIdentifierType> spec2)
    : Specification<TEntity, TIdentifierType>()
    where TEntity : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    private Expression<Func<TEntity, bool>>? _criteria;

    /// <inheritdoc />
    public override Expression<Func<TEntity, bool>> Criteria =>
        _criteria ??= SpecificationComposer.Combine<TEntity, TIdentifierType>(
            spec1, spec2, Expression.AndAlso);
}

/// <summary>
/// Composes two specifications with a logical OR, merging the two criteria by parameter
/// substitution (never <c>Expression.Invoke</c>) and caching the composed expression per instance.
/// See <see cref="AndSpecification{TEntity, TIdentifierType}"/> for why.
/// </summary>
/// <typeparam name="TEntity">The entity type this specification applies to.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
/// <param name="spec1">The left-hand specification.</param>
/// <param name="spec2">The right-hand specification.</param>
public sealed class OrSpecification<TEntity, TIdentifierType>(
    ISpecification<TEntity, TIdentifierType> spec1,
    ISpecification<TEntity, TIdentifierType> spec2)
    : Specification<TEntity, TIdentifierType>
    where TEntity : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    private Expression<Func<TEntity, bool>>? _criteria;

    /// <inheritdoc />
    public override Expression<Func<TEntity, bool>> Criteria =>
        _criteria ??= SpecificationComposer.Combine<TEntity, TIdentifierType>(
            spec1, spec2, Expression.OrElse);
}

/// <summary>
/// Negates a specification with <see cref="Expression.Not(Expression)"/>, reusing the inner
/// lambda's own parameter (never <c>Expression.Invoke</c>) and caching the composed expression
/// per instance. See <see cref="AndSpecification{TEntity, TIdentifierType}"/> for why.
/// </summary>
/// <typeparam name="TEntity">The entity type this specification applies to.</typeparam>
/// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
/// <param name="spec">The specification to negate.</param>
public sealed class NotSpecification<TEntity, TIdentifierType>(
    ISpecification<TEntity, TIdentifierType> spec)
    : Specification<TEntity, TIdentifierType>
    where TEntity : IBaseEntity<TIdentifierType>
    where TIdentifierType : notnull
{
    private Expression<Func<TEntity, bool>>? _criteria;

    /// <inheritdoc />
    public override Expression<Func<TEntity, bool>> Criteria =>
        _criteria ??= SpecificationComposer.Negate<TEntity, TIdentifierType>(spec);
}

/// <summary>
/// Shared body of the boolean composers: merges two criteria lambdas into one by rebinding the
/// right-hand parameter onto the left-hand one, so the composed tree contains no
/// <see cref="InvocationExpression"/>.
/// </summary>
internal static class SpecificationComposer
{
    /// <summary>Joins two specifications' criteria with a binary operator.</summary>
    /// <typeparam name="TEntity">The entity type the specifications apply to.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
    /// <param name="spec1">The left-hand specification.</param>
    /// <param name="spec2">The right-hand specification.</param>
    /// <param name="combine">The binary operator factory (<c>AndAlso</c> or <c>OrElse</c>).</param>
    /// <returns>The composed criteria expression.</returns>
    internal static Expression<Func<TEntity, bool>> Combine<TEntity, TIdentifierType>(
        ISpecification<TEntity, TIdentifierType> spec1,
        ISpecification<TEntity, TIdentifierType> spec2,
        Func<Expression, Expression, BinaryExpression> combine)
        where TEntity : IBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        ArgumentNullException.ThrowIfNull(spec1);
        ArgumentNullException.ThrowIfNull(spec2);

        var left = spec1.Criteria;
        var right = spec2.Criteria;
        var parameter = left.Parameters[0];

        var body = combine(
            left.Body,
            ParameterReplacer.Replace(right.Body, right.Parameters[0], parameter));

        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    /// <summary>Negates a specification's criteria in place, keeping its own parameter.</summary>
    /// <typeparam name="TEntity">The entity type the specification applies to.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
    /// <param name="spec">The specification to negate.</param>
    /// <returns>The negated criteria expression.</returns>
    internal static Expression<Func<TEntity, bool>> Negate<TEntity, TIdentifierType>(
        ISpecification<TEntity, TIdentifierType> spec)
        where TEntity : IBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        ArgumentNullException.ThrowIfNull(spec);

        var criteria = spec.Criteria;
        return Expression.Lambda<Func<TEntity, bool>>(
            Expression.Not(criteria.Body),
            criteria.Parameters[0]);
    }
}
