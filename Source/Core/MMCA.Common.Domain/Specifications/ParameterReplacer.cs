using System.Linq.Expressions;

namespace MMCA.Common.Domain.Specifications;

/// <summary>
/// Expression visitor that rebinds every occurrence of one <see cref="ParameterExpression"/> onto
/// another, so two independently authored lambdas can be merged into a single lambda body.
/// <para>
/// This is the composition primitive behind <see cref="AndSpecification{TEntity, TIdentifierType}"/>,
/// <see cref="OrSpecification{TEntity, TIdentifierType}"/> and
/// <see cref="NotSpecification{TEntity, TIdentifierType}"/>, and behind the cross-source specification
/// builder in the Application layer. Merging by substitution is deliberate: the obvious alternative,
/// <c>Expression.Invoke(spec.Criteria, parameter)</c>, leaves an <see cref="InvocationExpression"/> in
/// the tree, and several LINQ providers (Cosmos among them) refuse to translate one. Substitution
/// produces a tree indistinguishable from a hand-written predicate, so it translates everywhere.
/// </para>
/// </summary>
/// <remarks>
/// Internal by design: it is an implementation detail of specification composition, not part of the
/// framework's public surface. <c>MMCA.Common.Application</c> sees it through
/// <c>InternalsVisibleTo</c> so the cross-source builder shares this one visitor instead of carrying
/// a private copy.
/// </remarks>
internal sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
{
    /// <summary>
    /// Returns <paramref name="body"/> with every reference to <paramref name="from"/> replaced by
    /// <paramref name="to"/>.
    /// </summary>
    /// <param name="body">The expression body to rewrite.</param>
    /// <param name="from">The parameter to replace.</param>
    /// <param name="to">The parameter to replace it with.</param>
    /// <returns>The rewritten expression body.</returns>
    public static Expression Replace(Expression body, ParameterExpression from, ParameterExpression to)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        return ReferenceEquals(from, to) ? body : new ParameterReplacer(from, to).Visit(body);
    }

    /// <inheritdoc />
    protected override Expression VisitParameter(ParameterExpression node) =>
        node == from ? to : base.VisitParameter(node);
}
