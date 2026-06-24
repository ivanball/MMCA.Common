using System.Linq.Expressions;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string SpecificationBaseFullNamePrefix = "MMCA.Common.Domain.Specifications.Specification";

    /// <summary>
    /// A specification's <c>Criteria</c> must filter on the entity's own (scalar) columns, not by
    /// navigating to another entity (e.g. <c>s =&gt; s.Event.IsPublished</c>). In a polyglot /
    /// database-per-service setup the navigated entity may live in a different physical data source,
    /// where the join is not translatable and would throw at runtime (notably on Cosmos, where the
    /// cross-source navigation is degraded out of the model). Resolve the related keys first and
    /// filter by the foreign-key column instead — see <c>CrossSourceSpecification</c>.
    /// <para>
    /// This is an opt-in rule for polyglot-capable repos (single-engine repos need not run it). Only
    /// parameterless specifications can be analyzed — their <c>Criteria</c> is instantiated and
    /// inspected; specifications with constructor dependencies are skipped (the engine-portable
    /// "resolve keys then filter by FK" pattern does not navigate, so it is unaffected).
    /// </para>
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    public static void SpecificationsDoNotNavigateToOtherEntities(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var violations = new List<string>();

        var specTypes = map.OfLayer(Layer.Application)
            .Concat(map.OfLayer(Layer.Domain))
            .SelectMany(a => a.ConcreteClasses())
            .Where(t => t.HasBaseTypeStartingWith(SpecificationBaseFullNamePrefix));

        foreach (var specType in specTypes)
        {
            // Only parameterless specifications can be instantiated and inspected here.
            if (specType.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            var entityType = SpecificationEntityType(specType);
            if (entityType is null)
            {
                continue;
            }

            LambdaExpression? criteria;
            try
            {
                var instance = Activator.CreateInstance(specType);
                criteria = specType.GetProperty("Criteria")?.GetValue(instance) as LambdaExpression;
            }
            catch (Exception ex) when (ex is TargetInvocationException or MemberAccessException or MissingMethodException)
            {
                // Best-effort: a spec whose Criteria/constructor can't be evaluated standalone is skipped.
                continue;
            }

            if (criteria is null)
            {
                continue;
            }

            foreach (var navigated in new CrossEntityNavigationFinder(entityType).Find(criteria.Body))
            {
                violations.Add($"  - {specType.FullName} navigates to '{navigated}' (filter by a foreign-key column instead)");
            }
        }

        ArchitectureAssert.NoViolations(
            violations,
            "specifications must not navigate to another entity in their Criteria — in a polyglot setup the " +
            "navigated entity may live in a different data source, where the join is not translatable; resolve " +
            "the keys first and filter by the foreign-key column (CrossSourceSpecification)");
    }

    private static Type? SpecificationEntityType(Type specType)
    {
        for (var t = specType.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType
                && t.FullName?.StartsWith(SpecificationBaseFullNamePrefix, StringComparison.Ordinal) == true)
            {
                return t.GetGenericArguments()[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Walks a criteria expression and collects the names of entity types reached by navigating from a
    /// member access (single reference or collection), other than the specification's own entity type.
    /// </summary>
    private sealed class CrossEntityNavigationFinder(Type ownEntityType) : ExpressionVisitor
    {
        private readonly HashSet<string> _navigated = new(StringComparer.Ordinal);

        public HashSet<string> Find(Expression body)
        {
            Visit(body);
            return _navigated;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member is PropertyInfo property)
            {
                var targetEntity = EntityTypeOf(property.PropertyType);
                if (targetEntity is not null && targetEntity != ownEntityType)
                {
                    _navigated.Add(targetEntity.Name);
                }
            }

            return base.VisitMember(node);
        }

        private static Type? EntityTypeOf(Type propertyType)
        {
            if (propertyType.InheritsAuditableEntity())
            {
                return propertyType;
            }

            // Unwrap collection navigations (ICollection<TChild>, IReadOnlyCollection<TChild>, ...).
            if (propertyType.IsGenericType)
            {
                var elementType = propertyType.GetGenericArguments().FirstOrDefault();
                if (elementType is not null && elementType.InheritsAuditableEntity())
                {
                    return elementType;
                }
            }

            return null;
        }
    }
}
