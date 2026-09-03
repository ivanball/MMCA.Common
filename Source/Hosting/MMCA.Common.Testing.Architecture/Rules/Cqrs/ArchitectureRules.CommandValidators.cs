namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string CommandWithRequestInterfaceFullName = "MMCA.Common.Application.UseCases.Contracts.ICommandWithRequest`1";
    private const string ValidatorInterfaceFullName = "FluentValidation.IValidator`1";

    /// <summary>
    /// The Validating decorator runs FluentValidation before the transaction opens, so a command with
    /// no validator carries whatever the caller sent straight into the handler: the pipeline stage
    /// exists but has nothing to run. The gap is silent, because an unvalidated command behaves
    /// exactly like a valid one until bad input reaches the domain. This rule fails on any command
    /// that carries data, has a handler, and has no validation covering it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What counts as a command.</b> The command argument of every command handler interface
    /// implemented in the map's per-module Application assemblies, restricted to closed
    /// (non-generic-parameter) types that expose at least one public settable property. An
    /// <see langword="init"/> setter counts, so the usual positional-record command is inspected; a
    /// marker command with no payload has nothing to validate and is skipped.
    /// </para>
    /// <para>
    /// <b>What counts as coverage.</b> Either a concrete <c>IValidator&lt;TCommand&gt;</c>, or the
    /// <c>CommandRequestValidator</c> bridge: the command implements
    /// <c>ICommandWithRequest&lt;TRequest&gt;</c> AND a concrete <c>IValidator&lt;TRequest&gt;</c>
    /// exists. The bridge check requires BOTH halves on purpose, because
    /// <c>CommandRequestValidator</c> is registered for every such command but adds no rule when no
    /// request validator resolves, which is a validator that validates nothing.
    /// </para>
    /// <para>
    /// <b>Where it looks.</b> Commands and validators are both read from
    /// <see cref="IArchitectureMap.ModuleApplication"/>, mirroring the DI reality: the framework
    /// registers validators with <c>AddValidatorsFromAssembly(moduleAssembly)</c>, so a validator
    /// living anywhere else would not resolve at run time and must not count as coverage here either.
    /// </para>
    /// <para>
    /// <b>Limits.</b> A validator declared for a BASE command type does not count for its derived
    /// commands, matching FluentValidation's closed-type resolution. Abstract validators are ignored,
    /// since nothing registers them.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="allowedTypesAndNamespaces">
    /// Command type full names or namespace prefixes that are legitimately validation-free
    /// (an internal maintenance command, a command whose only field is a server-supplied identifier).
    /// </param>
    public static void CommandsHaveValidators(
        IArchitectureMap map,
        IReadOnlyCollection<string> allowedTypesAndNamespaces)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(allowedTypesAndNamespaces);

        var types = ApplicationTypes(map);
        var validated = ValidatedTypes(types);

        var violations = HandledCommands(types)
            .Where(command => !IsAllowed(command.FullName ?? command.Name, allowedTypesAndNamespaces)
                && !IsCovered(command, validated))
            .Select(command => $"  - {command.FullName} has no IValidator<> and embeds no validated request")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        ArchitectureAssert.NoViolations(violations,
            "every command that carries data must be validated before it reaches its handler: add a "
                + "FluentValidation validator for the command, or give it an ICommandWithRequest<T> "
                + "request that has one. Allowlist a command only when it genuinely has nothing to "
                + "check");
    }

    /// <summary>
    /// The number of commands the coverage rule inspects. Used by
    /// <see cref="CommandValidatorCoverageTestsBase"/> as a non-vacuity guard: a map that resolves to
    /// no module Application assemblies would otherwise let the gate pass without reading anything.
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <returns>The count of distinct data-carrying commands that have a handler.</returns>
    public static int HandledCommandCount(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return HandledCommands(ApplicationTypes(map)).Count;
    }

    /// <summary>Every loadable type in the map's per-module Application assemblies.</summary>
    private static List<Type> ApplicationTypes(IArchitectureMap map) =>
        [.. map.ModuleApplication().Distinct().SelectMany(assembly => assembly.LoadableTypes)];

    /// <summary>
    /// The distinct data-carrying command types handled by an <c>ICommandHandler</c> in scope.
    /// Open generic handler bases contribute nothing: their <c>TCommand</c> is a type parameter, not
    /// a command a repo ships.
    /// </summary>
    private static List<Type> HandledCommands(List<Type> types) =>
        [.. types
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(ClosedGenericArguments(CommandHandlerInterfaceFullName))
            .Select(arguments => arguments[0])
            .Where(command => !command.ContainsGenericParameters && HasSettablePublicProperty(command))
            .Distinct()];

    /// <summary>The types some concrete <c>IValidator&lt;T&gt;</c> in scope validates.</summary>
    private static HashSet<Type> ValidatedTypes(List<Type> types) =>
        [.. types
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(ClosedGenericArguments(ValidatorInterfaceFullName))
            .Select(arguments => arguments[0])];

    /// <summary>True when the command has a validator of its own, or a validated embedded request.</summary>
    private static bool IsCovered(Type command, HashSet<Type> validated) =>
        validated.Contains(command)
        || ClosedGenericArguments(CommandWithRequestInterfaceFullName)(command)
            .Any(arguments => validated.Contains(arguments[0]));

    /// <summary>
    /// A projection from a type to the generic arguments of every closed interface it implements
    /// whose open definition has the given full name. Matching by name keeps this package free of a
    /// compile reference to FluentValidation and to the framework's own Application layer.
    /// </summary>
    private static Func<Type, IEnumerable<Type[]>> ClosedGenericArguments(string openInterfaceFullName) =>
        type => type.GetInterfaces()
            .Where(i => i.IsGenericType
                && string.Equals(
                    i.GetGenericTypeDefinition().FullName,
                    openInterfaceFullName,
                    StringComparison.Ordinal))
            .Select(i => i.GetGenericArguments());

    /// <summary>
    /// True when the type exposes at least one public instance property with a public setter.
    /// <c>init</c> setters count: a positional record command is exactly the shape this rule guards.
    /// </summary>
    private static bool HasSettablePublicProperty(Type type) =>
        Array.Exists(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => property.SetMethod is { IsPublic: true });
}
