using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>The framework's error value type; every code in the catalog is built through it.</summary>
    private const string ErrorTypeFullName = "MMCA.Common.Shared.Abstractions.Error";

    /// <summary>
    /// The <c>Error</c> members whose first argument is the machine-readable code: every static
    /// factory on <c>Error</c>, plus the primary constructor for the rare <c>new Error(...)</c>.
    /// The record's copy constructor is excluded by the first-parameter-is-string check in
    /// <c>IsErrorFactory</c>, so <c>error with { Source = ... }</c> is not mistaken for a new code.
    /// </summary>
    private static readonly string[] ErrorFactoryNames =
    [
        "Validation",
        "Invariant",
        "NotFoundError",
        "Conflict",
        "Unauthorized",
        "Forbidden",
        "UnprocessableEntity",
        "Failure",
        "Unexpected",
        ".ctor",
    ];

    /// <summary>
    /// Error codes are the module's public vocabulary: a client switches on <c>Order.NotFound</c>, and
    /// a support ticket quotes it. Two modules that both ship <c>Item.Invalid</c> make that vocabulary
    /// ambiguous, and the ambiguity only surfaces in production. This rule fails when the same literal
    /// code is constructed by more than one type across the repo's module Domain and Application
    /// assemblies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scope.</b> Only the per-module Domain and Application assemblies are read
    /// (<see cref="IArchitectureMap.ModuleDomain"/> plus
    /// <see cref="IArchitectureMap.ModuleApplication"/>); framework layers are not the consumer's
    /// catalog.
    /// </para>
    /// <para>
    /// <b>Uniqueness is measured per declaring TYPE.</b> Reusing one code across two branches of the
    /// same class is one error with two exits and passes; the same code owned by two different types
    /// is the collision the rule exists to catch. That keeps a normal two-branch not-found handler out
    /// of the report.
    /// </para>
    /// <para>
    /// <b>Codes that are not literals</b> (built by concatenation, or read from a field) cannot be
    /// judged statically. They are neither passed nor failed: they are listed as UNVERIFIABLE in the
    /// failure message so the reader knows the catalog has a blind spot.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="allowedSharedCodes">
    /// Codes that are deliberately shared across types, typically the generic statics on <c>Error</c>
    /// itself (<c>Error.NotFound</c>, <c>Error.AlreadyDeleted</c>, <c>Error.InvalidEntityField</c>).
    /// </param>
    public static void ErrorCodesAreUnique(IArchitectureMap map, IReadOnlyCollection<string> allowedSharedCodes)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(allowedSharedCodes);

        var (sites, unverifiable) = CollectErrorCodes(map);

        var violations = sites
            .Where(site => !allowedSharedCodes.Contains(site.Code, StringComparer.Ordinal))
            .GroupBy(site => site.Code, StringComparer.Ordinal)
            .Select(group => (Code: group.Key, Owners: DistinctOwners(group)))
            .Where(entry => entry.Owners.Count > 1)
            .Select(entry => $"  - \"{entry.Code}\" is constructed by {string.Join(", ", entry.Owners)}")
            .Order(StringComparer.Ordinal)
            .ToList();

        ArchitectureAssert.NoViolations(violations, WithUnverifiable(
            "an error code is the module's public vocabulary, so one code must mean one thing across "
                + "the repo. Rename the collision, or add the code to the allowed shared codes when "
                + "the sharing is deliberate",
            unverifiable));
    }

    /// <summary>
    /// Error codes are namespaced by their owning module (<c>Sales.OrderNotFound</c>), which is what
    /// makes a code traceable back to the code that raised it and keeps two modules from colliding by
    /// accident. This rule fails on any literal code whose prefix
    /// <paramref name="isCodeAllowed"/> rejects.
    /// </summary>
    /// <remarks>
    /// The prefix convention is the consumer's, not the framework's: pass a delegate that closes over
    /// the repo's module names (what <see cref="ErrorCatalogTestsBase"/> does by default), or over a
    /// hand-written prefix set when the catalog is namespaced by aggregate rather than by module.
    /// Codes that are not literals are reported as UNVERIFIABLE exactly as in
    /// <see cref="ErrorCodesAreUnique"/>.
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="isCodeAllowed">Returns true when a code carries an acceptable prefix.</param>
    /// <param name="allowedSharedCodes">Codes exempt from the convention, checked before the delegate.</param>
    public static void ErrorCodesUseAnAllowedPrefix(
        IArchitectureMap map,
        Func<string, bool> isCodeAllowed,
        IReadOnlyCollection<string> allowedSharedCodes)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(isCodeAllowed);
        ArgumentNullException.ThrowIfNull(allowedSharedCodes);

        var (sites, unverifiable) = CollectErrorCodes(map);

        var violations = sites
            .Where(site => !allowedSharedCodes.Contains(site.Code, StringComparer.Ordinal)
                && !isCodeAllowed(site.Code))
            .Select(site => $"  - \"{site.Code}\" in {site.Owner}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        ArchitectureAssert.NoViolations(violations, WithUnverifiable(
            "an error code must be prefixed with the module that owns it, so the code alone says "
                + "where it came from and two modules cannot collide by accident",
            unverifiable));
    }

    /// <summary>
    /// The number of distinct literal error codes the scan found. Used by
    /// <see cref="ErrorCatalogTestsBase"/> as a non-vacuity guard: a map that resolves to no module
    /// assemblies would otherwise let both catalog rules pass without reading anything.
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <returns>The count of distinct literal codes across the module Domain and Application assemblies.</returns>
    public static int DistinctErrorCodeCount(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var (sites, _) = CollectErrorCodes(map);
        return sites.Select(site => site.Code).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>The distinct owning types of one code group, ordered for a stable message.</summary>
    private static List<string> DistinctOwners(IEnumerable<(string Code, string Owner)> group) =>
        [.. group.Select(site => site.Owner).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Appends the UNVERIFIABLE construction sites to a rule's failure reason.</summary>
    private static string WithUnverifiable(string reason, List<string> unverifiable) =>
        unverifiable.Count == 0
            ? reason
            : $"{reason}.{Environment.NewLine}UNVERIFIABLE (the code argument is not a literal, so it "
                + $"was neither passed nor failed):{Environment.NewLine}{string.Join(Environment.NewLine, unverifiable)}";

    /// <summary>
    /// Every literal error code constructed in the repo's module Domain and Application assemblies,
    /// paired with the type that owns it, plus the sites whose code could not be read statically.
    /// </summary>
    private static (List<(string Code, string Owner)> Sites, List<string> Unverifiable) CollectErrorCodes(
        IArchitectureMap map)
    {
        var sites = new List<(string Code, string Owner)>();
        var unverifiable = new List<string>();

        foreach (var location in ErrorCatalogAssemblyLocations(map))
        {
            using var module = ModuleDefinition.ReadModule(location);

            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    CollectErrorCodesIn(type, method, sites, unverifiable);
                }
            }
        }

        return (sites, unverifiable);
    }

    /// <summary>The on-disk module Domain and Application assemblies, de-duplicated.</summary>
    private static IEnumerable<string> ErrorCatalogAssemblyLocations(IArchitectureMap map) =>
        map.ModuleDomain()
            .Concat(map.ModuleApplication())
            .Select(assembly => assembly.Location)
            .Where(location => !string.IsNullOrEmpty(location) && File.Exists(location))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads every <c>Error</c> construction inside one method body.</summary>
    private static void CollectErrorCodesIn(
        TypeDefinition type,
        MethodDefinition method,
        List<(string Code, string Owner)> sites,
        List<string> unverifiable)
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference callee || !IsErrorFactory(callee))
            {
                continue;
            }

            var codeArgument = PurePushArgument(instruction, callee.Parameters.Count);
            if (codeArgument is not null && codeArgument.OpCode == OpCodes.Ldstr && codeArgument.Operand is string code)
            {
                sites.Add((code, OwnerName(type)));
            }
            else
            {
                unverifiable.Add($"  ? {OwnerName(type)}.{method.Name}");
            }
        }
    }

    /// <summary>True when the callee is an <c>Error</c> factory (or constructor) taking a code first.</summary>
    private static bool IsErrorFactory(MethodReference callee) =>
        string.Equals(callee.DeclaringType?.FullName, ErrorTypeFullName, StringComparison.Ordinal)
        && ErrorFactoryNames.Contains(callee.Name, StringComparer.Ordinal)
        && callee.Parameters.Count > 0
        && string.Equals(callee.Parameters[0].ParameterType.FullName, "System.String", StringComparison.Ordinal);

    /// <summary>
    /// The instruction that pushed the FIRST argument of a call, or null when the arguments are not
    /// all single-instruction pushes. Walking back one instruction per argument is exact for the
    /// overwhelmingly common shape (literals, nulls, locals, fields); anything computed makes the
    /// offset unsound, so the walk bails and the site is reported as UNVERIFIABLE instead of guessed.
    /// </summary>
    private static Instruction? PurePushArgument(Instruction call, int argumentCount)
    {
        var current = call;
        for (var remaining = argumentCount; remaining > 0; remaining--)
        {
            current = current.Previous;
            if (current is null || !IsPurePush(current))
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>True when an instruction pushes exactly one value and pops nothing.</summary>
    private static bool IsPurePush(Instruction instruction) =>
        instruction.OpCode.StackBehaviourPop == StackBehaviour.Pop0
        && instruction.OpCode.StackBehaviourPush is StackBehaviour.Push1
            or StackBehaviour.Pushi
            or StackBehaviour.Pushi8
            or StackBehaviour.Pushr4
            or StackBehaviour.Pushr8
            or StackBehaviour.Pushref;

    /// <summary>
    /// The type credited with a code. Compiler-generated nested types (lambda closures, async state
    /// machines) are collapsed onto the type that declares them, so ownership reads like source.
    /// </summary>
    private static string OwnerName(TypeDefinition type)
    {
        var owner = type;
        while (owner.DeclaringType is not null && owner.Name.StartsWith('<'))
        {
            owner = owner.DeclaringType;
        }

        return owner.FullName;
    }
}
