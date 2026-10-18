using System.Collections.Frozen;
using Mono.Cecil;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// The two exceptions a member throws when it refuses the contract it claims to honour: the
    /// operation is not supported by this subtype, or it was never written.
    /// </summary>
    private static readonly FrozenSet<string> ContractRefusalExceptionFullNames = FrozenSet.Create(
        StringComparer.Ordinal,
        "System.NotSupportedException",
        "System.NotImplementedException");

    /// <summary>
    /// Liskov substitutability: a member that overrides a base-class virtual or abstract member, or
    /// implements an interface member, must not throw <c>System.NotSupportedException</c> or
    /// <c>System.NotImplementedException</c>. A caller holding the base type or the interface was
    /// promised the operation; a subtype that refuses it breaks every caller that substitutes it in,
    /// and the break surfaces only at run time, on the path nobody tested with that subtype.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it looks.</b> Reflection decides which members are substitutable (an override is a
    /// method whose base definition lives on another type; an implementation is a target of the
    /// type's interface map declared on the type itself), and the IL of each one is read through the
    /// same <see langword="throw"/> scan the domain-throw rule uses, so only an exception constructed
    /// in place and thrown is judged. A method that is neither an override nor an implementation may
    /// throw what it likes.
    /// </para>
    /// <para>
    /// <b>What it does not touch.</b> The bodies the compiler writes (the read-only wrappers emitted
    /// for a collection expression, an iterator's <c>Reset</c>) are skipped, as in the domain-throw
    /// rule. An <see langword="async"/> override is judged on its own body, not on its state machine.
    /// </para>
    /// <para>
    /// <b>Scope.</b> A module-bearing (consumer) map scans only its module assemblies, so the
    /// framework is not re-judged in every consumer; the framework's own module-less map scans
    /// every layer.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="excludedMembers">
    /// Fully-qualified members (<c>Namespace.Type.Method</c>, the reflection full name of the type, so a
    /// nested type reads <c>Namespace.Outer+Inner.Method</c> and a property getter
    /// <c>Namespace.Type.get_Length</c>) whose refusal is the deliberate, reviewed choice.
    /// </param>
    public static void SubstitutableMembersDoNotRefuseTheContract(
        IArchitectureMap map,
        IReadOnlySet<string> excludedMembers)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(excludedMembers);

        var violations = new List<string>();

        foreach (var assembly in SubstitutabilityAssemblies(map))
        {
            using var module = ModuleDefinition.ReadModule(assembly.Location);

            foreach (var method in SubstitutableMethods(assembly))
            {
                var memberName = $"{method.DeclaringType!.FullName}.{method.Name}";
                if (excludedMembers.Contains(memberName)
                    || module.LookupToken(method.MetadataToken) is not MethodDefinition { HasBody: true } definition
                    || IsCompilerSynthesized(definition))
                {
                    continue;
                }

                var refusal = ThrownExceptionTypes(definition)
                    .FirstOrDefault(thrown => thrown is not null && ContractRefusalExceptionFullNames.Contains(thrown));
                if (refusal is not null)
                {
                    violations.Add($"  - {memberName} throws {refusal}");
                }
            }
        }

        ArchitectureAssert.NoViolations(
            violations,
            "an override or interface implementation must honour the contract it claims (Liskov "
                + "substitutability): throwing NotSupportedException or NotImplementedException breaks "
                + "every caller that substitutes this type for its base or interface. Implement the "
                + "member, narrow the abstraction so the type no longer promises it, or list the member "
                + "in ExcludedMembers with a comment saying why refusing is right there");
    }

    /// <summary>
    /// The on-disk assemblies the map owns: module assemblies in a consumer map, every layer in the
    /// framework's module-less map.
    /// </summary>
    private static IEnumerable<Assembly> SubstitutabilityAssemblies(IArchitectureMap map)
    {
        var includeFrameworkLayers = map.ModuleNames.Count == 0;
        return map.Layers
            .Where(l => includeFrameworkLayers || l.Module.Length > 0)
            .Select(l => l.Assembly)
            .Distinct()
            .Where(a => !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location));
    }

    /// <summary>
    /// Every method declared in the assembly that overrides a base-class member or implements an
    /// interface member, each listed once.
    /// </summary>
    private static IEnumerable<MethodInfo> SubstitutableMethods(Assembly assembly)
    {
        const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var type in assembly.LoadableTypes.Where(t => !t.IsInterface))
        {
            var overrides = type.GetMethods(declared)
                .Where(m => m.IsVirtual && m.GetBaseDefinition().DeclaringType != type);

            var implementations = type.GetInterfaces()
                .SelectMany(i => type.GetInterfaceMap(i).TargetMethods)
                .Where(m => m.DeclaringType == type);

            foreach (var method in overrides.Concat(implementations).DistinctBy(m => m.MetadataToken))
            {
                yield return method;
            }
        }
    }
}
