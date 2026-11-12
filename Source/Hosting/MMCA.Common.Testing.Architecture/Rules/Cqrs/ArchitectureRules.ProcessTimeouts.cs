namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string ProcessTimeoutCommandInterfaceFullName = "MMCA.Common.Application.ProcessManagers.IProcessTimeoutCommand`1";
    private const string ProcessTimeoutHandlerBaseFullName = "MMCA.Common.Application.ProcessManagers.ProcessTimeoutHandlerBase`3";
    private const string InternalCommandNameAttributeFullName = "MMCA.Common.Application.InternalCommands.InternalCommandNameAttribute";

    /// <summary>
    /// A stored internal-command row resolves its handler by the command's stable name, or by its CLR
    /// type name when it declares none, so a timeout command without <c>[InternalCommandName]</c>
    /// strands every row already queued the day its type is renamed or moved. This rule fails on any
    /// concrete process timeout command in the map's module Application assemblies that does not
    /// declare the attribute itself.
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    internal static void ProcessTimeoutCommandsDeclareAStableInternalCommandName(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var violations = ProcessTimeoutCommands(ApplicationTypes(map))
            .Where(command => !command.CustomAttributes.Any(attribute => string.Equals(
                attribute.AttributeType.FullName,
                InternalCommandNameAttributeFullName,
                StringComparison.Ordinal)))
            .Select(command => $"  - {command.FullName} declares no [InternalCommandName]")
            .Order(StringComparer.Ordinal)
            .ToList();

        ArchitectureAssert.NoViolations(violations,
            "every process timeout command must declare a stable [InternalCommandName], so the rows "
                + "already queued still resolve after its type is renamed or moved");
    }

    /// <summary>
    /// A fired timeout is resolved to ONE handler, and the process-manager primitive's handler is a
    /// subclass of <c>ProcessTimeoutHandlerBase</c>. This rule fails on any concrete process timeout
    /// command in the map's module Application assemblies that is handled by no such subclass (its rows
    /// would dead-letter) or by more than one (the resolution would be ambiguous). A plain
    /// <c>ICommandHandler</c> for a timeout command does not count.
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    internal static void ProcessTimeoutCommandsHaveExactlyOneTimeoutHandler(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var types = ApplicationTypes(map);
        var handlersByCommand = types
            .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false })
            .Select(handler => (Handler: handler, Command: TimeoutHandlerCommand(handler)))
            .Where(pair => pair.Command is not null)
            .ToLookup(pair => pair.Command, pair => pair.Handler);

        var violations = ProcessTimeoutCommands(types)
            .Select(command => (Command: command, Count: handlersByCommand[command].Count()))
            .Where(entry => entry.Count != 1)
            .Select(entry => $"  - {entry.Command.FullName} has {entry.Count} ProcessTimeoutHandlerBase subclasses (expected exactly one)")
            .Order(StringComparer.Ordinal)
            .ToList();

        ArchitectureAssert.NoViolations(violations,
            "every process timeout command must be handled by exactly one ProcessTimeoutHandlerBase "
                + "subclass: none dead-letters its rows, more than one makes the resolution ambiguous");
    }

    /// <summary>
    /// The number of process timeout commands the two rules above inspect. Used by
    /// <see cref="ProcessTimeoutConventionTestsBase"/> as a non-vacuity guard.
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    /// <returns>The count of distinct concrete process timeout commands in the map's module Application assemblies.</returns>
    internal static int ProcessTimeoutCommandCount(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return ProcessTimeoutCommands(ApplicationTypes(map)).Count;
    }

    /// <summary>The distinct concrete types in scope that implement <c>IProcessTimeoutCommand&lt;T&gt;</c>.</summary>
    private static List<Type> ProcessTimeoutCommands(List<Type> types) =>
        [.. types
            .Where(t => !t.IsAbstract && !t.IsInterface && !t.ContainsGenericParameters
                && ClosedGenericArguments(ProcessTimeoutCommandInterfaceFullName)(t).Any())
            .Distinct()];

    /// <summary>
    /// The timeout command a handler type closes <c>ProcessTimeoutHandlerBase</c> over, found on its
    /// base-type chain by full name, or <see langword="null"/> when it does not derive from that base.
    /// </summary>
    private static Type? TimeoutHandlerCommand(Type handler)
    {
        for (var baseType = handler.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.IsGenericType
                && string.Equals(
                    baseType.GetGenericTypeDefinition().FullName,
                    ProcessTimeoutHandlerBaseFullName,
                    StringComparison.Ordinal))
            {
                return baseType.GetGenericArguments()[0];
            }
        }

        return null;
    }
}
