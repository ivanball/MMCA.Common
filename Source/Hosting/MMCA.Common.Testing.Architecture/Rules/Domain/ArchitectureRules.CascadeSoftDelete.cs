using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>The aggregate-root base, without Cecil's generic-arity marker.</summary>
    private const string AggregateRootBaseFullName = "MMCA.Common.Domain.Entities.AuditableAggregateRootEntity";

    /// <summary>The auditable-entity base every child of an aggregate derives from, arity stripped.</summary>
    private const string AuditableEntityBaseFullName = "MMCA.Common.Domain.Entities.AuditableBaseEntity";

    /// <summary>The framework helper that cascades a soft-delete across a child collection.</summary>
    private const string CascadeHelperName = "DeleteChildren";

    /// <summary>The soft-delete member on the entity base.</summary>
    private const string DeleteMemberName = "Delete";

    /// <summary>
    /// The generic collection types a child collection is declared as. A child collection held in
    /// anything else (a dictionary, a custom collection type) is out of scope rather than
    /// mis-reported.
    /// </summary>
    private static readonly string[] ChildCollectionTypeNames =
    [
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.HashSet`1",
    ];

    /// <summary>
    /// Soft-delete does not cascade by itself. Setting <c>IsDeleted = true</c> on an aggregate root
    /// hides the root behind the global query filter and leaves every child row ACTIVE: an order line
    /// of a deleted order, a session of a deleted event. Nothing reads those rows through the root any
    /// more, so the orphans are invisible until a report, an export or a per-child query surfaces them,
    /// and an erasure request that walks the child table finds data whose owner was "deleted" months
    /// ago. The database cannot help either, because a soft delete is an ordinary UPDATE: there is no
    /// <c>ON DELETE CASCADE</c> to fire.
    /// <para>
    /// This rule fails when an aggregate root that OWNS children does not cascade the delete to them:
    /// every type deriving from <c>AuditableAggregateRootEntity&lt;T&gt;</c> that declares a collection
    /// of <c>AuditableBaseEntity&lt;T&gt;</c> children must declare a <c>Delete()</c> override whose
    /// body deletes those children, either through the framework's
    /// <c>DeleteChildren&lt;TChild, TChildId&gt;</c> helper or by calling the child's own
    /// <c>Delete()</c>.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it looks.</b> Neither NetArchTest nor reflection can see a CALL inside a method body, so
    /// the rule reads the IL of every assembly the map registers (through the Mono.Cecil already
    /// carried by NetArchTest) and inspects the <c>Delete()</c> override's <c>call</c>/<c>callvirt</c>
    /// operands. Bases and callees are matched by full NAME with the generic arity stripped, so the
    /// package keeps its deliberate zero-reference stance toward the framework it governs, and an
    /// aggregate whose base lives in an assembly the map does not register is still recognized.
    /// </para>
    /// <para>
    /// <b>What counts as a child collection.</b> An instance field declared on the aggregate whose type
    /// is one of <see cref="ChildCollectionTypeNames"/> over an element type deriving from
    /// <c>AuditableBaseEntity&lt;T&gt;</c>. Auto-properties are covered by the same pass, because an
    /// auto-property IS a compiler-generated instance field (the backing field's name is normalized
    /// back to the property name for the report). The aggregate's own <c>_domainEvents</c> is not a
    /// child collection twice over: it is declared on the base rather than on the aggregate, and
    /// <c>IDomainEvent</c> is not an entity.
    /// </para>
    /// <para>
    /// <b>What counts as cascading.</b> A direct call, inside the <c>Delete()</c> override, to
    /// <c>DeleteChildren</c> (the helper on the aggregate base), or to a zero-argument <c>Delete</c> on
    /// something other than <see langword="this"/>. The second form is what a hand-rolled
    /// <c>foreach (var line in _lines) line.Delete();</c> compiles to. <c>base.Delete()</c> is
    /// deliberately NOT accepted: C# emits it as a non-virtual <c>call</c> to a virtual method, which
    /// nothing else in C# produces, so the rule can tell "delete myself" from "delete my children"
    /// without guessing.
    /// </para>
    /// <para>
    /// <b>Allowlist entries</b> are type full names (<c>Some.Ns.Domain.Basket</c>) or namespaces
    /// (<c>Some.Ns.Domain.Legacy</c>); a namespace entry covers everything under it. Matching is
    /// ordinal and case-sensitive, exactly as <see cref="HardDeletesOnlyInAllowedTypes"/> matches.
    /// </para>
    /// <para>
    /// <b>Limits.</b> (1) Only DIRECT calls in the override are read, the same visibility limit the
    /// hard-delete rule documents: an aggregate that delegates the loop to a private helper is
    /// reported, and inlining the loop (or calling <c>DeleteChildren</c>) is the fix. (2) The rule
    /// proves that a cascade EXISTS, not that it covers every collection: an aggregate with two child
    /// collections that cascades only one passes here, and a unit test on the aggregate is what pins
    /// the second. (3) Only fields declared ON the aggregate are inspected, so a collection inherited
    /// from an intermediate abstract entity is out of scope. (4) Children reached by navigation from a
    /// child (grandchildren) are the child's own cascade to own.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="allowedTypes">
    /// Type full names or namespace prefixes where NOT cascading is the deliberate, reviewed choice.
    /// An empty list requires every child-bearing aggregate to cascade.
    /// </param>
    public static void AggregatesCascadeSoftDeleteToChildren(
        IArchitectureMap map,
        IReadOnlyCollection<string> allowedTypes)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(allowedTypes);

        var modules = new List<ModuleDefinition>();
        try
        {
            foreach (var location in ScannableAssemblyLocations(map))
            {
                modules.Add(ModuleDefinition.ReadModule(location));
            }

            var index = new CallGraphIndex(modules);

            var violations = index.Types
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && !IsAllowed(type.FullName, allowedTypes)
                    && DerivesFrom(index, type, AggregateRootBaseFullName))
                .Select(type => (Aggregate: type, Collections: ChildCollectionsOf(type, index)))
                .Where(found => found.Collections.Count > 0)
                .Select(found => DescribeCascadeGap(found.Aggregate, found.Collections))
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .ToList();

            ArchitectureAssert.NoViolations(violations,
                "a soft-delete is an ordinary UPDATE, so it does not cascade: an aggregate root that "
                    + "owns children must delete them in its own Delete() override, through "
                    + "DeleteChildren<TChild, TChildId>(...) or by calling each child's Delete(). "
                    + "Without it the root disappears behind the query filter and its child rows stay "
                    + "active and orphaned, invisible to every read through the root but still present "
                    + "for exports, reports and erasure requests");
        }
        finally
        {
            foreach (var module in modules)
            {
                module.Dispose();
            }
        }
    }

    /// <summary>
    /// The violation line for an aggregate that owns children, or null when it cascades correctly.
    /// </summary>
    private static string? DescribeCascadeGap(TypeDefinition aggregate, IReadOnlyCollection<string> collections)
    {
        var names = string.Join(", ", collections);

        var deleteOverride = aggregate.Methods.FirstOrDefault(m =>
            !m.IsStatic
            && m.HasBody
            && m.Parameters.Count == 0
            && string.Equals(m.Name, DeleteMemberName, StringComparison.Ordinal));

        if (deleteOverride is null)
        {
            return $"  - {aggregate.FullName}: no Delete() override, so its children ({names}) stay active";
        }

        return CascadesToChildren(deleteOverride)
            ? null
            : $"  - {aggregate.FullName}: Delete() never deletes a child, so its children ({names}) stay active";
    }

    /// <summary>
    /// True when the override's own IL cascades: a call to the <c>DeleteChildren</c> helper, or a
    /// zero-argument <c>Delete</c> invoked on something other than <see langword="this"/>.
    /// </summary>
    private static bool CascadesToChildren(MethodDefinition deleteOverride) =>
        deleteOverride.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference callee
            && (string.Equals(callee.Name, CascadeHelperName, StringComparison.Ordinal)
                || IsChildDeleteCall(instruction, callee)));

    /// <summary>
    /// True when the instruction deletes a CHILD: a zero-argument <c>Delete</c> reached virtually.
    /// <c>base.Delete()</c> is the one thing C# compiles to a non-virtual <c>call</c> on a virtual
    /// member, so excluding that opcode separates "delete myself" from "delete my children" exactly.
    /// </summary>
    private static bool IsChildDeleteCall(Instruction instruction, MethodReference callee) =>
        string.Equals(callee.Name, DeleteMemberName, StringComparison.Ordinal)
        && callee.Parameters.Count == 0
        && instruction.OpCode == OpCodes.Callvirt;

    /// <summary>
    /// The names of the aggregate's own child-entity collections, with auto-property backing fields
    /// reported under their property name.
    /// </summary>
    private static IReadOnlyCollection<string> ChildCollectionsOf(TypeDefinition aggregate, CallGraphIndex index) =>
        [.. aggregate.Fields
            .Where(field => !field.IsStatic && IsChildEntityCollection(field.FieldType, index))
            .Select(field => DeclaredMemberName(field.Name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>True when the type is a generic collection over an auditable child entity.</summary>
    private static bool IsChildEntityCollection(TypeReference fieldType, CallGraphIndex index)
    {
        if (fieldType is not GenericInstanceType collection
            || collection.GenericArguments.Count != 1
            || !ChildCollectionTypeNames.Contains(collection.ElementType.FullName, StringComparer.Ordinal))
        {
            return false;
        }

        var element = index.Find(collection.GenericArguments[0]);
        return element is not null && DerivesFrom(index, element, AuditableEntityBaseFullName);
    }

    /// <summary>
    /// True when any base type of <paramref name="type"/> carries the given full name once the
    /// generic-arity marker is stripped. The comparison is by NAME, so a base declared in an assembly
    /// outside the scan is still recognized; only the walk THROUGH such a base stops.
    /// </summary>
    private static bool DerivesFrom(CallGraphIndex index, TypeDefinition type, string baseFullName)
    {
        for (var current = type; current?.BaseType is not null; current = index.Find(current.BaseType))
        {
            if (string.Equals(WithoutArity(CallGraphIndex.ElementFullName(current.BaseType)), baseFullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A full name with Cecil's generic-arity marker removed (<c>Foo`1</c> to <c>Foo</c>).</summary>
    private static string WithoutArity(string fullName)
    {
        var tick = fullName.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? fullName[..tick] : fullName;
    }

    /// <summary>
    /// The source-level member name behind a field: an auto-property's backing field is named
    /// <c>&lt;Lines&gt;k__BackingField</c> in metadata and is reported as <c>Lines</c>.
    /// </summary>
    private static string DeclaredMemberName(string fieldName)
    {
        if (!fieldName.StartsWith('<'))
        {
            return fieldName;
        }

        var close = fieldName.IndexOf('>', StringComparison.Ordinal);
        return close > 1 ? fieldName[1..close] : fieldName;
    }
}
