using Mono.Cecil;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string EntityFrameworkNamespacePrefix = "Microsoft.EntityFrameworkCore";

    /// <summary>The EF Core members that erase a row instead of soft-deleting it.</summary>
    private static readonly string[] HardDeleteMemberNames =
        ["Remove", "RemoveRange", "ExecuteDelete", "ExecuteDeleteAsync"];

    /// <summary>
    /// The EF Core entity-set types whose <c>Remove</c>/<c>RemoveRange</c> erase a row. Scoping the
    /// two common names to these declaring types keeps every unrelated <c>Remove</c> (a dictionary,
    /// a list, a cache) out of the rule.
    /// </summary>
    private static readonly string[] EntitySetTypeNames =
    [
        "Microsoft.EntityFrameworkCore.DbContext",
        "Microsoft.EntityFrameworkCore.DbSet`1",
        "Microsoft.EntityFrameworkCore.Internal.InternalDbSet`2",
        "Microsoft.EntityFrameworkCore.ChangeTracking.LocalView`1",
    ];

    /// <summary>
    /// Soft-delete is the framework's deletion model: an entity sets <c>IsDeleted = true</c> and the
    /// global query filter hides it, so the row survives for audit, restore and erasure accounting.
    /// A hard delete bypasses all of that. This rule fails when EF Core's erasing members
    /// (<c>DbSet.Remove</c>, <c>DbSet.RemoveRange</c>, <c>DbContext.Remove</c>,
    /// <c>RemoveRange</c>, <c>ExecuteDelete</c>, <c>ExecuteDeleteAsync</c>) are invoked from a type
    /// outside <paramref name="allowedTypesAndNamespaces"/>, which is the repo's short, reviewable
    /// list of the places where erasing a row IS the requirement: retention purge jobs, outbox and
    /// audit-trail cleanup, and GDPR erasure handlers (ADR-005).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it looks.</b> Neither NetArchTest nor reflection can see a CALL inside a method body,
    /// so the rule reads the IL of every assembly the map registers (through the Mono.Cecil already
    /// carried by NetArchTest) and inspects each <c>call</c>/<c>callvirt</c> operand. Callees are
    /// matched by full name, exactly like the other framework types this package detects, so the
    /// package keeps its deliberate zero-reference stance toward EF Core.
    /// </para>
    /// <para>
    /// <b>Allowlist entries</b> are type full names (<c>Some.Ns.OutboxCleanupService</c>) or
    /// namespaces (<c>Some.Ns.Purge</c>); a namespace entry covers everything under it. An entry
    /// also covers the compiler-generated async state machines and closures of the type it names,
    /// since those are nested inside it. Matching is ordinal and case-sensitive.
    /// </para>
    /// <para>
    /// <b>Limits.</b> The rule sees only direct calls compiled into the scanned assemblies. A hard
    /// delete reached through an interface the repo owns (for example a repository's own
    /// <c>ExecuteDeleteAsync</c>) is caught at the implementing type, not at the caller, so that
    /// implementation belongs on the allowlist and the abstraction stays free to be used.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="allowedTypesAndNamespaces">
    /// Type full names or namespace prefixes where a hard delete is the deliberate, reviewed choice.
    /// An empty list bans hard deletes outright.
    /// </param>
    public static void HardDeletesOnlyInAllowedTypes(
        IArchitectureMap map,
        IReadOnlyCollection<string> allowedTypesAndNamespaces)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(allowedTypesAndNamespaces);

        var violations = new List<string>();

        foreach (var location in ScannableAssemblyLocations(map))
        {
            using var module = ModuleDefinition.ReadModule(location);

            foreach (var type in module.GetTypes())
            {
                violations.AddRange(HardDeleteCallsIn(type, allowedTypesAndNamespaces));
            }
        }

        ArchitectureAssert.NoViolations(violations,
            "deletion goes through soft-delete (IsDeleted = true), so EF Core's erasing members must "
                + "only be called from the purge/erasure types the repo allowlists. A hard delete "
                + "elsewhere silently drops the row from audit, restore and erasure accounting");
    }

    /// <summary>The on-disk assemblies of the map, de-duplicated and skipping anything unreadable.</summary>
    private static IEnumerable<string> ScannableAssemblyLocations(IArchitectureMap map) =>
        map.Layers
            .Select(l => l.Assembly.Location)
            .Where(location => !string.IsNullOrEmpty(location) && File.Exists(location))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The disallowed hard-delete call sites inside one type's method bodies.</summary>
    private static IEnumerable<string> HardDeleteCallsIn(
        TypeDefinition type,
        IReadOnlyCollection<string> allowedTypesAndNamespaces)
    {
        if (IsAllowed(type.FullName, allowedTypesAndNamespaces))
        {
            yield break;
        }

        foreach (var method in type.Methods)
        {
            if (!method.HasBody)
            {
                continue;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MethodReference callee && IsHardDelete(callee))
                {
                    yield return $"  - {type.FullName}.{method.Name} calls {callee.DeclaringType.Name}.{callee.Name}";
                }
            }
        }
    }

    /// <summary>True when the callee is one of EF Core's row-erasing members.</summary>
    private static bool IsHardDelete(MethodReference callee)
    {
        if (!HardDeleteMemberNames.Contains(callee.Name, StringComparer.Ordinal))
        {
            return false;
        }

        var declaringType = callee.DeclaringType?.FullName;
        if (declaringType is null || !declaringType.StartsWith(EntityFrameworkNamespacePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // ExecuteDelete/ExecuteDeleteAsync are unambiguous wherever EF declares them (the relational
        // queryable extensions today, a provider's own extensions tomorrow). Remove/RemoveRange are
        // ordinary collection names, so they only count on an entity-set type.
        return callee.Name is "ExecuteDelete" or "ExecuteDeleteAsync"
            || Array.Exists(EntitySetTypeNames, name => declaringType.StartsWith(name, StringComparison.Ordinal));
    }

    /// <summary>
    /// True when a type full name is covered by an allowlist entry, either exactly or as a member of
    /// an allowlisted namespace, generic type or containing type (Cecil separates nested types
    /// with <c>/</c> and generic arity with <c>`</c>).
    /// </summary>
    private static bool IsAllowed(string typeFullName, IReadOnlyCollection<string> allowedTypesAndNamespaces) =>
        allowedTypesAndNamespaces.Any(entry =>
            string.Equals(typeFullName, entry, StringComparison.Ordinal)
            || typeFullName.StartsWith(entry + ".", StringComparison.Ordinal)
            || typeFullName.StartsWith(entry + "/", StringComparison.Ordinal)
            || typeFullName.StartsWith(entry + "`", StringComparison.Ordinal));
}
