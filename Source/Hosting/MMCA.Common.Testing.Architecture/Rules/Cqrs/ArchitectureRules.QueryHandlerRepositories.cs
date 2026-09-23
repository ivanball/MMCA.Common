using Mono.Cecil;

namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    private const string UnitOfWorkInterfaceFullName = "MMCA.Common.Application.Interfaces.Infrastructure.Persistence.IUnitOfWork";
    private const string GetRepositoryMethodName = "GetRepository";

    /// <summary>
    /// A query handler reads, so it asks the unit of work for a read repository:
    /// <c>IUnitOfWork.GetReadRepository</c>, never <c>IUnitOfWork.GetRepository</c>. The write
    /// repository hands a query the mutation surface (add, remove, tracked reads) it has no business
    /// using, and it only exists for aggregate roots, so a read of a child entity cannot use it at all.
    /// The rule fails on any <c>GetRepository</c> call inside a type implementing
    /// <c>IQueryHandler&lt;,&gt;</c> in the map's Application assemblies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it looks.</b> A call site is invisible to reflection, so the rule reads IL through the
    /// Mono.Cecil NetArchTest already carries, and matches the callee by name and declaring type full
    /// name (the package keeps its zero-reference stance toward the framework). Every method of the
    /// handler is read together with its compiler-generated nested types, which is where an async
    /// body (the state machine) and a lambda body (the display class) actually live.
    /// </para>
    /// <para>
    /// <b>What it does not see.</b> A call made in a helper type the handler delegates to is outside
    /// the handler, so it is not reported. Abstract handler bases are scanned like concrete handlers,
    /// so a base that reaches for the write repository is reported once, on the base.
    /// </para>
    /// </remarks>
    /// <param name="map">The repo's architecture map.</param>
    /// <param name="allowedTypesAndNamespaces">
    /// Type full names or namespace prefixes exempt from the rule (the adoption ratchet for a repo with
    /// existing offenders). An empty list exempts nothing.
    /// </param>
    public static void QueryHandlersUseReadRepositories(
        IArchitectureMap map,
        IReadOnlyCollection<string> allowedTypesAndNamespaces)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(allowedTypesAndNamespaces);

        var modules = new List<ModuleDefinition>();
        try
        {
            foreach (var location in ApplicationAssemblyLocations(map))
            {
                modules.Add(ModuleDefinition.ReadModule(location));
            }

            var index = new CallGraphIndex(modules);
            var handlers = index.Types
                .Where(type => !type.IsInterface
                    && !type.Name.StartsWith('<')
                    && index.Implements(type, QueryHandlerInterfaceFullName))
                .ToList();

            handlers.Should().NotBeEmpty(
                because: "the read-repository rule found no IQueryHandler implementations in the map's Application assemblies, so it would verify nothing (wrong assembly under Layer.Application?)");

            var violations = handlers
                .Where(handler => !IsAllowed(handler.FullName, allowedTypesAndNamespaces))
                .SelectMany(handler => WriteRepositoryCallsIn(handler)
                    .Select(method => $"  - {handler.FullName}.{method} calls IUnitOfWork.GetRepository"))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            ArchitectureAssert.NoViolations(
                violations,
                "a query handler reads, so it takes IUnitOfWork.GetReadRepository, not GetRepository: the "
                    + "write repository hands a query the mutation surface it must not use. Switch the call, "
                    + "or allowlist the handler when it genuinely needs the write surface");
        }
        finally
        {
            foreach (var module in modules)
            {
                module.Dispose();
            }
        }
    }

    /// <summary>The on-disk Application assemblies of the map (framework and per-module), de-duplicated.</summary>
    private static IEnumerable<string> ApplicationAssemblyLocations(IArchitectureMap map) =>
        map.OfLayer(Layer.Application)
            .Select(assembly => assembly.Location)
            .Where(location => !string.IsNullOrEmpty(location) && File.Exists(location))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The names of the handler's source methods whose bodies (their own IL, or the IL of the
    /// compiler-generated nested type they were rewritten into) call <c>IUnitOfWork.GetRepository</c>.
    /// </summary>
    private static IEnumerable<string> WriteRepositoryCallsIn(TypeDefinition handler)
    {
        foreach (var type in SelfAndNestedTypes(handler))
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var callsWriteRepository = method.Body.Instructions.Any(instruction =>
                    instruction.Operand is MethodReference callee
                    && string.Equals(callee.Name, GetRepositoryMethodName, StringComparison.Ordinal)
                    && string.Equals(CallGraphIndex.ElementFullName(callee.DeclaringType), UnitOfWorkInterfaceFullName, StringComparison.Ordinal));

                if (callsWriteRepository)
                {
                    yield return SourceMemberName(type, method);
                }
            }
        }
    }

    /// <summary>The type and every type nested inside it, at any depth.</summary>
    private static IEnumerable<TypeDefinition> SelfAndNestedTypes(TypeDefinition type)
    {
        yield return type;

        foreach (var nested in type.NestedTypes.SelectMany(SelfAndNestedTypes))
        {
            yield return nested;
        }
    }
}
