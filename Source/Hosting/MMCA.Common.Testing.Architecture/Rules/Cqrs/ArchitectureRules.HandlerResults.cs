namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    // Full names of the framework CQRS handler interfaces and Result types, matched by string so
    // the rule library keeps zero compile dependencies on the framework assemblies.
    private const string CommandHandlerInterfaceFullName = "MMCA.Common.Application.UseCases.ICommandHandler`2";
    private const string QueryHandlerInterfaceFullName = "MMCA.Common.Application.UseCases.IQueryHandler`2";
    private const string ResultFullName = "MMCA.Common.Shared.Abstractions.Result";
    private const string GenericResultFullName = "MMCA.Common.Shared.Abstractions.Result`1";

    /// <summary>
    /// The map's <see cref="Layer.Application"/> assemblies contain at least one concrete
    /// command/query handler. This is the non-vacuity guard for the TResult rules: a map whose
    /// Application assemblies register no handlers (wrong assembly pinned, scan drift) would
    /// otherwise let those rules pass while checking nothing.
    /// </summary>
    public static void ApplicationLayersDeclareHandlers(IArchitectureMap map)
    {
        map.OfLayer(Layer.Application).Should().NotBeEmpty(
            because: "the handler-result rules reflect over the map's Application assemblies; register them under Layer.Application");

        var handlers = HandlerInterfaces(map, CommandHandlerInterfaceFullName)
            .Concat(HandlerInterfaces(map, QueryHandlerInterfaceFullName))
            .ToList();

        handlers.Should().NotBeEmpty(
            because: "the map's Application assemblies must contain at least one concrete ICommandHandler/IQueryHandler implementation — an empty handler set means the TResult rules verify nothing (wrong assembly in the map?)");
    }

    /// <summary>
    /// Every concrete <c>ICommandHandler&lt;TCommand, TResult&gt;</c> in the map's Application
    /// assemblies (framework decorators excluded) closes TResult over
    /// <c>MMCA.Common.Shared.Abstractions.Result</c> or a type derived from it. The runtime
    /// decorator pipeline can only short-circuit (feature gate, validation) by fabricating a
    /// failure via <c>ResultFailureFactory</c>, which throws <c>InvalidOperationException</c> for
    /// any other TResult — this rule surfaces that deferred compile-time constraint at test time.
    /// </summary>
    public static void CommandHandlersReturnResult(IArchitectureMap map) =>
        HandlersReturnResult(map, CommandHandlerInterfaceFullName, "ICommandHandler");

    /// <summary>
    /// Every concrete <c>IQueryHandler&lt;TQuery, TResult&gt;</c> in the map's Application
    /// assemblies (framework decorators excluded) closes TResult over
    /// <c>MMCA.Common.Shared.Abstractions.Result</c> or a type derived from it (same rationale as
    /// <see cref="CommandHandlersReturnResult"/>).
    /// </summary>
    public static void QueryHandlersReturnResult(IArchitectureMap map) =>
        HandlersReturnResult(map, QueryHandlerInterfaceFullName, "IQueryHandler");

    private static void HandlersReturnResult(IArchitectureMap map, string interfaceFullName, string interfaceDisplayName)
    {
        var violations = HandlerInterfaces(map, interfaceFullName)
            .Where(h => !IsResultType(h.ClosedInterface.GetGenericArguments()[1]))
            .Select(h => $"  - {h.Handler.FullName} implements {interfaceDisplayName}<{h.ClosedInterface.GetGenericArguments()[0].Name}, {h.ClosedInterface.GetGenericArguments()[1].Name}> — TResult must be Result or Result<T>");

        ArchitectureAssert.NoViolations(violations,
            $"every {interfaceDisplayName} TResult must be MMCA.Common.Shared.Abstractions.Result (or derived) — the decorator pipeline's short-circuit paths throw InvalidOperationException for any other TResult (see ResultFailureFactory)");
    }

    /// <summary>
    /// Concrete handler implementations (with their closed handler interface) across the map's
    /// Application assemblies, excluding the framework's own pipeline decorators.
    /// </summary>
    private static IEnumerable<(Type Handler, Type ClosedInterface)> HandlerInterfaces(IArchitectureMap map, string interfaceFullName) =>
        map.OfLayer(Layer.Application)
            .SelectMany(a => a.ConcreteClasses)
            .Where(t => !t.SimpleName.EndsWith("Decorator", StringComparison.Ordinal))
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType
                    && string.Equals(i.GetGenericTypeDefinition().FullName, interfaceFullName, StringComparison.Ordinal))
                .Select(i => (Handler: t, ClosedInterface: i)));

    /// <summary>True when the type is Result, Result{T}, or derives from either.</summary>
    private static bool IsResultType(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var definition = current.IsGenericType ? current.GetGenericTypeDefinition() : current;
            if (definition.FullName is ResultFullName or GenericResultFullName)
            {
                return true;
            }
        }

        return false;
    }
}
