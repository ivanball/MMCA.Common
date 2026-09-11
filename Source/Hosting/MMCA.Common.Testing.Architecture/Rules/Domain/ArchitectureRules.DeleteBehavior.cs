namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>Annotation <c>RestrictDeleteByDefaultConvention</c> stamps on every foreign key.</summary>
    private const string DeleteBehaviorSourceAnnotation = "MMCA:DeleteBehaviorSource";

    /// <summary>Annotation value marking a delete behavior an entity configuration chose.</summary>
    private const string ExplicitDeleteBehaviorSource = "Explicit";

    /// <summary>Annotation value marking a delete behavior the framework convention supplied.</summary>
    private const string ConventionDeleteBehaviorSource = "Convention";

    /// <summary>
    /// Every cascading relationship in a finalized EF model was opted into on purpose: its delete
    /// behavior carries <c>MMCA:DeleteBehaviorSource = Explicit</c>, meaning an entity configuration
    /// called <c>OnDelete(DeleteBehavior.Cascade)</c> rather than inheriting EF's cascading default
    /// for a required relationship.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cascade nobody wrote down is a delete that runs below the aggregate: it removes child rows
    /// in the database without the invariant checks the aggregate root exists to enforce, without
    /// honoring soft delete, and without raising the domain events the rest of the system reacts to.
    /// Making the opt-in visible in the model is what turns "the schema happens to cascade here" into
    /// a decision a reviewer can see.
    /// </para>
    /// <para>
    /// Ownership foreign keys are exempt and skipped: an owned type has no identity apart from its
    /// owner, and EF requires that cascade rather than offering it.
    /// </para>
    /// </remarks>
    /// <param name="model">A finalized EF Core model (<c>dbContext.Model</c>).</param>
    public static void CascadingForeignKeysAreExplicitlyOptedIn(object model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var violations = ForeignKeyDeleteFacts(model)
            .Where(fact => !fact.IsOwnership
                && (string.Equals(fact.DeleteBehavior, "Cascade", StringComparison.Ordinal)
                    || string.Equals(fact.DeleteBehavior, "ClientCascade", StringComparison.Ordinal))
                && !string.Equals(fact.Source, ExplicitDeleteBehaviorSource, StringComparison.Ordinal))
            .Select(fact =>
                $"  - {fact.Description} deletes with {fact.DeleteBehavior}, but its delete-behavior source is "
                + $"{fact.Source ?? "absent"} rather than {ExplicitDeleteBehaviorSource}");

        ArchitectureAssert.NoViolations(violations,
            "a cascading delete must be an explicit decision in the entity configuration "
            + "(.OnDelete(DeleteBehavior.Cascade) with the business reason beside it): an inherited cascade "
            + "deletes child rows below the aggregate's invariants, below the soft-delete filter and below "
            + "the domain events the framework raises");
    }

    /// <summary>
    /// Every relationship whose delete behavior the framework convention supplied
    /// (<c>MMCA:DeleteBehaviorSource = Convention</c>) restricts: deleting a parent that still has
    /// children fails loudly instead of taking them with it or silently orphaning them.
    /// </summary>
    /// <remarks>
    /// This is the other half of the same guarantee. The first rule says nothing cascades by accident;
    /// this one says the default everything else lands on is the safe one, so a model cannot drift into
    /// a third behavior (a client-side null-out, say) without anybody choosing it.
    /// </remarks>
    /// <param name="model">A finalized EF Core model (<c>dbContext.Model</c>).</param>
    public static void ConventionForeignKeysRestrictDeletes(object model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var violations = ForeignKeyDeleteFacts(model)
            .Where(fact => string.Equals(fact.Source, ConventionDeleteBehaviorSource, StringComparison.Ordinal)
                && !string.Equals(fact.DeleteBehavior, "Restrict", StringComparison.Ordinal))
            .Select(fact =>
                $"  - {fact.Description} was left to the convention but deletes with {fact.DeleteBehavior}, not Restrict");

        ArchitectureAssert.NoViolations(violations,
            "the restrict-by-default convention owns every relationship nobody configured, so a foreign key "
            + "it stamped must restrict; a different behavior means the convention did not run, ran too early, "
            + "or was overridden after it ran");
    }

    /// <summary>
    /// One foreign key's delete-behavior facts, read off a finalized model.
    /// </summary>
    /// <param name="Description">Dependent entity and columns, for the failure message.</param>
    /// <param name="DeleteBehavior">The behavior's name (<c>Cascade</c>, <c>Restrict</c>, ...).</param>
    /// <param name="IsOwnership">Whether the relationship is an ownership, whose cascade EF owns.</param>
    /// <param name="Source">The <c>MMCA:DeleteBehaviorSource</c> annotation, or null when absent.</param>
    private sealed record ForeignKeyDeleteFact(
        string Description,
        string DeleteBehavior,
        bool IsOwnership,
        string? Source);

    /// <summary>
    /// Walks a finalized EF model through its metadata members by name.
    /// </summary>
    /// <remarks>
    /// This package carries no EF Core reference on purpose (see the csproj comment): the rule library
    /// must not drag EF Core, MassTransit and the provider stack into every consumer's
    /// <c>*.Architecture.Tests</c> project just to read four members off a model. The members read here
    /// (<c>GetEntityTypes</c>, <c>GetDeclaredForeignKeys</c>, <c>IsOwnership</c>, <c>DeleteBehavior</c>,
    /// <c>Properties</c>, <c>FindAnnotation</c>) are EF's stable public metadata surface; a missing one
    /// fails with a message naming it rather than silently reporting no violations.
    /// </remarks>
    /// <param name="model">A finalized EF Core model.</param>
    /// <returns>One record per declared foreign key in the model.</returns>
    private static IEnumerable<ForeignKeyDeleteFact> ForeignKeyDeleteFacts(object model)
    {
        foreach (var entityType in Enumerate(Invoke(model, "GetEntityTypes")))
        {
            var entityName = Read(entityType, "Name") as string ?? entityType.GetType().Name;

            foreach (var foreignKey in Enumerate(Invoke(entityType, "GetDeclaredForeignKeys")))
            {
                var columns = string.Join(
                    ", ",
                    Enumerate(Read(foreignKey, "Properties")).Select(p => Read(p, "Name") as string ?? "?"));

                yield return new ForeignKeyDeleteFact(
                    $"{entityName}({columns})",
                    Read(foreignKey, "DeleteBehavior")?.ToString() ?? "unknown",
                    Read(foreignKey, "IsOwnership") is true,
                    AnnotationValue(foreignKey, DeleteBehaviorSourceAnnotation));
            }
        }
    }

    /// <summary>Reads one annotation's value off an EF metadata object, or null when it carries none.</summary>
    /// <param name="annotatable">The EF metadata object.</param>
    /// <param name="name">The annotation name.</param>
    /// <returns>The annotation value as a string, or null.</returns>
    private static string? AnnotationValue(object annotatable, string name)
    {
        var method = FindMethod(annotatable.GetType(), "FindAnnotation", [typeof(string)])
            ?? throw new InvalidOperationException(
                $"{annotatable.GetType().Name} exposes no FindAnnotation(string): the object passed to the "
                + "delete-behavior rules is not an EF Core model.");

        var annotation = method.Invoke(annotatable, [name]);
        return annotation is null ? null : Read(annotation, "Value")?.ToString();
    }

    /// <summary>Invokes a parameterless member by name, failing with a message that names it.</summary>
    /// <param name="target">The EF metadata object.</param>
    /// <param name="methodName">The parameterless method to invoke.</param>
    /// <returns>The method's return value.</returns>
    private static object? Invoke(object target, string methodName)
    {
        var method = FindMethod(target.GetType(), methodName, Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                $"{target.GetType().Name} exposes no {methodName}(): the object passed to the delete-behavior "
                + "rules is not an EF Core model.");

        return method.Invoke(target, null);
    }

    /// <summary>Reads a property by name, failing with a message that names it.</summary>
    /// <param name="target">The EF metadata object.</param>
    /// <param name="propertyName">The property to read.</param>
    /// <returns>The property value.</returns>
    private static object? Read(object target, string propertyName)
    {
        var property = FindProperty(target.GetType(), propertyName)
            ?? throw new InvalidOperationException(
                $"{target.GetType().Name} exposes no {propertyName}: the object passed to the delete-behavior "
                + "rules is not an EF Core model.");

        return property.GetValue(target);
    }

    /// <summary>
    /// Finds a method on a type or on one of the interfaces it implements. The interface pass is what
    /// makes the walk work at all: EF's runtime metadata classes implement most of the read-only
    /// metadata surface explicitly, so the member is invisible on the concrete type.
    /// </summary>
    /// <param name="type">The runtime type to search.</param>
    /// <param name="name">The method name.</param>
    /// <param name="parameterTypes">The method's parameter types.</param>
    /// <returns>The method, or null when neither the type nor its interfaces declare it.</returns>
    private static MethodInfo? FindMethod(Type type, string name, Type[] parameterTypes) =>
        type.GetMethod(name, parameterTypes)
        ?? type.GetInterfaces()
            .Select(contract => contract.GetMethod(name, parameterTypes))
            .FirstOrDefault(method => method is not null);

    /// <summary>Finds a property on a type or on one of the interfaces it implements.</summary>
    /// <param name="type">The runtime type to search.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The property, or null when neither the type nor its interfaces declare it.</returns>
    private static PropertyInfo? FindProperty(Type type, string name) =>
        type.GetProperty(name)
        ?? type.GetInterfaces()
            .Select(contract => contract.GetProperty(name))
            .FirstOrDefault(property => property is not null);

    /// <summary>Materializes a non-generic sequence returned by an EF metadata member.</summary>
    /// <param name="sequence">The value to enumerate.</param>
    /// <returns>The elements, or empty when the value is null.</returns>
    private static IEnumerable<object> Enumerate(object? sequence) =>
        sequence is System.Collections.IEnumerable items
            ? items.Cast<object>()
            : [];
}
