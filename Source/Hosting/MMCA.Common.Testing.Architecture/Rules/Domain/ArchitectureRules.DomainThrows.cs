using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string ExtensionMarkerAttributeFullName = "System.Runtime.CompilerServices.ExtensionMarkerAttribute";
    private const string CompilerGeneratedAttributeFullName = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

    /// <summary>
    /// The exceptions a Result-pattern domain still throws: argument guards, which report a caller
    /// BUG rather than a business outcome. A caller cannot recover from passing null, so there is
    /// nothing for a <c>Result</c> to carry.
    /// </summary>
    private static readonly string[] ArgumentGuardExceptionFullNames =
    [
        "System.ArgumentException",
        "System.ArgumentNullException",
        "System.ArgumentOutOfRangeException",
    ];

    /// <summary>
    /// ADR-013: the domain reports failure by returning <c>Result</c>/<c>Result&lt;T&gt;</c>, never by
    /// throwing. A thrown business failure skips the Result pipeline entirely: it bypasses
    /// <c>Result.Combine</c> invariant composition, turns a 4xx outcome into a 500, and costs an
    /// exception unwind on a path that is not exceptional. This rule fails on any <see langword="throw"/> in the
    /// map's Domain assemblies whose exception is not one of the argument guards in
    /// <see cref="ArgumentGuardExceptionFullNames"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it looks.</b> Neither NetArchTest nor reflection can see a <see langword="throw"/> inside a method
    /// body, so the rule reads IL (through the Mono.Cecil already carried by NetArchTest) and, for
    /// every <see langword="throw"/> instruction, reads the type of the exception constructed immediately before
    /// it. Types are matched by full name, so the package keeps its zero-reference stance toward the
    /// framework.
    /// </para>
    /// <para>
    /// <b>What it does not touch.</b> A bare <c>throw;</c> compiles to the distinct <c>rethrow</c>
    /// opcode and is ignored, so preserving a caught exception stays free. The
    /// <c>ArgumentNullException.ThrowIfNull</c> family emits a plain <c>call</c> with no <see langword="throw"/>
    /// in the caller, so the modern guard style is invisible to the scan and always passes. The
    /// bodies the compiler writes are skipped too: the skeleton members of a C# extension block, and
    /// the explicit interface implementations on a compiler-generated type (the read-only wrappers
    /// emitted for a collection expression, an iterator's Reset). Their NotSupportedException is not
    /// anyone's code, and a lambda or async body is still read.
    /// </para>
    /// <para>
    /// <b>Throws it cannot judge.</b> When the thrown value was not constructed in place
    /// (<c>throw prepared;</c>, a field, a factory call), the exception type is not knowable from the
    /// instruction stream. Those sites are listed as UNVERIFIABLE in the failure message rather than
    /// passed or failed.
    /// </para>
    /// <para>
    /// <b>Allowlist entries</b> are type full names or namespace prefixes, matched exactly as
    /// <see cref="HardDeletesOnlyInAllowedTypes"/> matches them. They exist for the plumbing a domain
    /// cannot express as a <c>Result</c>: an ORM-facing shim, a generated partial, a guard clause that
    /// genuinely signals a programming error with <c>InvalidOperationException</c>.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="allowedTypesAndNamespaces">
    /// Type full names or namespace prefixes where throwing is the deliberate, reviewed choice.
    /// An empty list allows nothing beyond the argument guards.
    /// </param>
    public static void DomainThrowsOnlyArgumentGuards(
        IArchitectureMap map,
        IReadOnlyCollection<string> allowedTypesAndNamespaces)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(allowedTypesAndNamespaces);

        var violations = new List<string>();
        var unverifiable = new List<string>();

        foreach (var location in DomainAssemblyLocations(map))
        {
            using var module = ModuleDefinition.ReadModule(location);

            foreach (var type in module.GetTypes())
            {
                if (IsAllowed(type.FullName, allowedTypesAndNamespaces))
                {
                    continue;
                }

                foreach (var method in type.Methods.Where(m => m.HasBody && !IsCompilerSynthesized(m)))
                {
                    CollectThrowsIn(type, method, violations, unverifiable);
                }
            }
        }

        ArchitectureAssert.NoViolations(
            violations,
            WithUnverifiable(
                "the domain reports failure by returning Result, not by throwing (ADR-013). A thrown "
                    + "business failure skips invariant composition and surfaces as a 500 instead of "
                    + "the outcome the caller can act on. Return a Result.Failure with an Error, or "
                    + "allowlist the type when the throw really is plumbing",
                "the thrown value was not constructed in place, so its type is not knowable from the instruction stream",
                unverifiable));
    }

    /// <summary>The on-disk Domain assemblies of the map (framework and per-module), de-duplicated.</summary>
    private static IEnumerable<string> DomainAssemblyLocations(IArchitectureMap map) =>
        map.OfLayer(Layer.Domain)
            .Select(assembly => assembly.Location)
            .Where(location => !string.IsNullOrEmpty(location) && File.Exists(location))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records every disallowed or unreadable <see langword="throw"/> inside one method body.</summary>
    private static void CollectThrowsIn(
        TypeDefinition type,
        MethodDefinition method,
        List<string> violations,
        List<string> unverifiable)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode != OpCodes.Throw)
            {
                continue;
            }

            var exceptionType = ConstructedExceptionType(instruction);
            if (exceptionType is null)
            {
                unverifiable.Add($"  ? {OwnerName(type)}.{method.Name}");
            }
            else if (!ArgumentGuardExceptionFullNames.Contains(exceptionType, StringComparer.Ordinal))
            {
                violations.Add($"  - {OwnerName(type)}.{method.Name} throws {exceptionType}");
            }
        }
    }

    /// <summary>
    /// The full name of the exception constructed immediately before a <see langword="throw"/>, or null when the
    /// thrown value came from somewhere else. Debug builds interleave <c>nop</c>s, which are skipped.
    /// </summary>
    private static string? ConstructedExceptionType(Instruction throwInstruction)
    {
        var previous = throwInstruction.Previous;
        while (previous is not null && previous.OpCode == OpCodes.Nop)
        {
            previous = previous.Previous;
        }

        return previous?.OpCode == OpCodes.Newobj && previous.Operand is MethodReference constructor
            ? constructor.DeclaringType.FullName
            : null;
    }

    /// <summary>
    /// True for a method body the compiler wrote, whose <c>NotSupportedException</c> nobody typed and
    /// nobody can remove. Two shapes reach a Domain assembly:
    /// <list type="number">
    /// <item>
    /// the skeleton members of a C# <c>extension(T)</c> block, which carry
    /// <c>ExtensionMarkerAttribute</c> and exist only to hold metadata (the real body lives in an
    /// unspeakably-named sibling);
    /// </item>
    /// <item>
    /// the explicit interface implementations on a compiler-generated type: the read-only list
    /// wrappers emitted for a collection expression throw from every mutating <c>IList</c> member, and
    /// an iterator state machine throws from <c>IEnumerator.Reset</c>.
    /// </item>
    /// </list>
    /// The dotted-name test keeps this narrow: a lambda body and an async <c>MoveNext</c> are not
    /// explicit implementations, so a throw a developer typed inside one is still reported.
    /// </summary>
    private static bool IsCompilerSynthesized(MethodDefinition method) =>
        HasAttribute(method.CustomAttributes, ExtensionMarkerAttributeFullName)
        || HasAttribute(method.DeclaringType.CustomAttributes, CompilerGeneratedAttributeFullName)
            && method.Name.Contains('.', StringComparison.Ordinal);

    /// <summary>True when the attribute collection carries the named attribute.</summary>
    private static bool HasAttribute(IEnumerable<CustomAttribute> attributes, string attributeFullName) =>
        attributes.Any(a => string.Equals(a.AttributeType.FullName, attributeFullName, StringComparison.Ordinal));
}
