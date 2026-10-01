using System.Runtime.CompilerServices;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Cqrs.ConstructorDependencies;

/// <summary>
/// Single-responsibility ceiling (rubric section 1) over the framework itself, driven by the shared
/// <see cref="ConstructorDependencyCountTestsBase"/>. MMCA.Common has no business modules, so the
/// base's module populations are empty here; this subclass points the three populations at the
/// framework assemblies instead and measures public AND protected constructors, because most of the
/// framework's widest classes are abstract bases (<c>AuthenticationServiceBase</c>,
/// <c>*HandlerBase</c>, <c>*ControllerBase</c>) whose only constructors are protected.
/// <list type="bullet">
///   <item>Services: every top-level class, abstract or not, in <c>MMCA.Common.Application</c> and
///   <c>MMCA.Common.Infrastructure</c>, held to <see cref="MaxConstructorDependencies"/> (7).</item>
///   <item>Controllers: every <c>ControllerBase</c>-derived class, abstract or not, in the framework
///   API assembly, held to the current framework high-water mark.</item>
///   <item>Handlers: every <c>ICommandHandler</c>/<c>IQueryHandler</c> implementation, abstract or
///   not, in the framework Application assembly (the decorators included), held to the current
///   framework high-water mark.</item>
/// </list>
/// Optional parameters count: an optional collaborator is still a collaborator the class coordinates.
/// The ceilings are ratchets; lower them as remediation lands, never raise one to turn a run green.
/// </summary>
public sealed class FrameworkConstructorDependencyTests : ConstructorDependencyCountTestsBase
{
    private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string CommandHandlerFullName = "MMCA.Common.Application.UseCases.Contracts.ICommandHandler`2";
    private const string QueryHandlerFullName = "MMCA.Common.Application.UseCases.Contracts.IQueryHandler`2";

    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    protected override int MaxConstructorDependencies => 7;

    // Framework high-water mark when this gate landed: CrudEntityControllerBase, InboxController and
    // UserAccountAuthControllerBase (5 each).
    protected override int MaxControllerConstructorDependencies => 5;

    // Framework high-water mark when this gate landed: ResetPasswordHandlerBase (7).
    protected override int MaxHandlerConstructorDependencies => 7;

    protected override IEnumerable<Type> ScannedServices =>
        FrameworkClasses(Map.OfLayer(Layer.Application).Concat(Map.OfLayer(Layer.Infrastructure)));

    protected override IEnumerable<Type> ScannedControllers =>
        FrameworkClasses(Map.OfLayer(Layer.Api)).Where(IsController);

    protected override IEnumerable<Type> ScannedHandlers =>
        FrameworkClasses(Map.OfLayer(Layer.Application)).Where(IsCommandOrQueryHandler);

    protected override IEnumerable<ConstructorInfo> MeasuredConstructors(Type type) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(static c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly);

    /// <summary>
    /// Every top-level (so public or internal), non-compiler-generated class of the given assemblies,
    /// abstract classes included. Record classes are excluded: a positional record's constructor
    /// parameters are data fields, not injected collaborators, so they say nothing about how many
    /// responsibilities a class coordinates (the compiler emits <c>&lt;Clone&gt;$</c> only on records).
    /// </summary>
    private static IEnumerable<Type> FrameworkClasses(IEnumerable<Assembly> assemblies) =>
        assemblies
            .Distinct()
            .SelectMany(static a => a.GetTypes())
            .Where(static t => t is { IsClass: true, IsNested: false }
                && !(t.IsAbstract && t.IsSealed)
                && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                && t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is null);

    private static bool IsController(Type type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (string.Equals(baseType.FullName, ControllerBaseFullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCommandOrQueryHandler(Type type) =>
        type.GetInterfaces().Any(static i => i.IsGenericType
            && i.GetGenericTypeDefinition().FullName is { } contract
            && (string.Equals(contract, CommandHandlerFullName, StringComparison.Ordinal)
                || string.Equals(contract, QueryHandlerFullName, StringComparison.Ordinal)));
}
