using System.Reflection;

namespace MMCA.Common.Application.UseCases;

/// <summary>
/// Why a CQRS request's declared contract and its handler's signature disagree.
/// </summary>
public enum CqrsContractMismatchKind
{
    /// <summary>
    /// The request declares one result type through <see cref="ICommand{TResult}"/> or
    /// <see cref="IQuery{TResult}"/>, and its handler returns a different one.
    /// </summary>
    ResultType = 0,

    /// <summary>
    /// The request is declared as a command but handled by an <see cref="IQueryHandler{TQuery, TResult}"/>
    /// (or declared as a query and handled by an <see cref="ICommandHandler{TCommand, TResult}"/>).
    /// </summary>
    HandlerKind = 1,
}

/// <summary>
/// One disagreement between a request's declared CQRS contract and the handler written for it.
/// </summary>
/// <param name="RequestType">The command or query type carrying the marker.</param>
/// <param name="HandlerType">The concrete handler implementation.</param>
/// <param name="DeclaredResultType">The result type the request declared through its marker.</param>
/// <param name="HandlerResultType">The result type the handler actually returns.</param>
/// <param name="Kind">Which kind of disagreement this is.</param>
public sealed record CqrsContractMismatch(
    Type RequestType,
    Type HandlerType,
    Type DeclaredResultType,
    Type HandlerResultType,
    CqrsContractMismatchKind Kind)
{
    /// <summary>
    /// Renders the mismatch as a single human-readable line, suitable for a fitness-test failure message.
    /// </summary>
    /// <returns>A description naming the request, the handler and both result types.</returns>
    public string Describe() => Kind switch
    {
        CqrsContractMismatchKind.HandlerKind =>
            $"'{RequestType.Name}' declares the opposite kind of contract to the one '{HandlerType.Name}' handles: an ICommand marker needs an ICommandHandler and an IQuery marker needs an IQueryHandler.",
        CqrsContractMismatchKind.ResultType =>
            $"'{RequestType.Name}' declares result type '{DeclaredResultType.Name}' but its handler '{HandlerType.Name}' returns '{HandlerResultType.Name}'.",
        _ =>
            $"'{RequestType.Name}' and its handler '{HandlerType.Name}' disagree.",
    };
}

/// <summary>
/// Reflection-only inspector that pairs request types carrying the opt-in <see cref="ICommand{TResult}"/> /
/// <see cref="IQuery{TResult}"/> markers with the handlers written for them, and reports every
/// disagreement.
/// </summary>
/// <remarks>
/// <para>
/// Built for architecture fitness tests: a repo can call
/// <see cref="FindContractMismatches(System.Collections.Generic.IEnumerable{Assembly})"/> over its
/// module assemblies and fail the build when the list is non-empty. Requests that carry no marker
/// are ignored entirely, so adopting the markers stays gradual and a repo with zero adoption sees an
/// empty list rather than a wall of failures.
/// </para>
/// <para>
/// Plain reflection over public interface maps, no IL reading. Open generic handler definitions
/// (the framework's own decorators, and any generic handler base) are skipped: their request type
/// argument is a type parameter, not a request.
/// </para>
/// </remarks>
public static class CqrsContractInspector
{
    /// <summary>
    /// Finds every disagreement between a marked request's declared contract and its handler's signature.
    /// </summary>
    /// <param name="assemblies">
    /// The assemblies to scan for handler implementations. A params collection, so a caller can list
    /// module assemblies inline or pass an existing sequence.
    /// </param>
    /// <returns>The mismatches found, empty when every marked request agrees with its handler.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assemblies"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<CqrsContractMismatch> FindContractMismatches(params IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var mismatches = new List<CqrsContractMismatch>();

        var candidates = assemblies
            .SelectMany(GetLoadableTypes)
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false });

        foreach (var candidate in candidates)
        {
            InspectHandler(candidate, mismatches);
        }

        return mismatches;
    }

    private static void InspectHandler(Type handlerType, List<CqrsContractMismatch> mismatches)
    {
        foreach (var handlerInterface in handlerType.GetInterfaces())
        {
            if (!handlerInterface.IsGenericType)
                continue;

            var definition = handlerInterface.GetGenericTypeDefinition();
            var isCommandHandler = definition == typeof(ICommandHandler<,>);

            if (!isCommandHandler && definition != typeof(IQueryHandler<,>))
                continue;

            var handlerArguments = handlerInterface.GetGenericArguments();
            var mismatch = Compare(handlerType, handlerArguments[0], handlerArguments[1], isCommandHandler);

            if (mismatch is not null)
                mismatches.Add(mismatch);
        }
    }

    private static CqrsContractMismatch? Compare(
        Type handlerType,
        Type requestType,
        Type handlerResultType,
        bool isCommandHandler)
    {
        var declaredAsCommand = FindMarker(requestType, typeof(ICommand<>));
        var declaredAsQuery = FindMarker(requestType, typeof(IQuery<>));

        var matching = isCommandHandler ? declaredAsCommand : declaredAsQuery;
        var opposite = isCommandHandler ? declaredAsQuery : declaredAsCommand;

        if (matching is null)
        {
            // The request carries no marker of the handler's own kind. A marker of the opposite kind
            // is a genuine pairing bug, while no marker at all just means the request never opted in.
            return opposite is null
                ? null
                : new CqrsContractMismatch(
                    requestType, handlerType, opposite, handlerResultType, CqrsContractMismatchKind.HandlerKind);
        }

        return matching == handlerResultType
            ? null
            : new CqrsContractMismatch(
                requestType, handlerType, matching, handlerResultType, CqrsContractMismatchKind.ResultType);
    }

    /// <summary>
    /// Returns the result type the request declared through <paramref name="markerDefinition"/>,
    /// or <see langword="null"/> when it carries no such marker.
    /// </summary>
    private static Type? FindMarker(Type requestType, Type markerDefinition)
    {
        foreach (var candidate in requestType.GetInterfaces())
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == markerDefinition)
                return candidate.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>
    /// Enumerates an assembly's types, degrading to the types that did load when a missing transitive
    /// reference makes <see cref="Assembly.GetTypes"/> throw.
    /// </summary>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
