using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.UseCases;
using Xunit;

namespace MMCA.Common.Testing;

/// <summary>
/// Opt-in fitness function for the ADR-014 decorator pipeline: builds a real
/// <see cref="ServiceCollection"/> through the repo's own registration sequence, resolves the
/// decorated command/query handlers from the built provider, and asserts the runtime nesting order
/// is exactly the documented pipeline —
/// commands: FeatureGate → Logging → Caching → Validating → Transactional → Handler;
/// queries: FeatureGate → Logging → Caching → Handler. Because Scrutor's <c>TryDecorate</c> applies
/// decorators in reverse registration order, an innocent-looking reorder of the
/// <c>AddApplicationDecorators()</c> lines (or a module scan registered after it) silently changes
/// runtime behavior; this base turns that into a test failure.
/// <para>
/// Subclass with one representative command/query pair and implement
/// <see cref="ConfigureServices"/> to (1) register test doubles for the decorator dependencies
/// (<c>IFeatureManager</c>, <c>ICorrelationContext</c>, <c>ICacheService</c>, <c>IUnitOfWork</c>,
/// <c>ILogger&lt;&gt;</c>), then (2) run the repo's real registration sequence, e.g.
/// <c>services.AddApplication().ScanModuleApplicationServices&lt;MyMarker&gt;().AddApplicationDecorators()</c>,
/// where the scanned assembly contains a concrete handler for each of the four type parameters.
/// </para>
/// <para>
/// The chain is unwrapped via reflection over each decorator's private inner-handler field, so it
/// verifies the actual constructed object graph, not just the registration list.
/// </para>
/// </summary>
/// <typeparam name="TCommand">A command with a registered concrete handler.</typeparam>
/// <typeparam name="TCommandResult">That command handler's TResult.</typeparam>
/// <typeparam name="TQuery">A query with a registered concrete handler.</typeparam>
/// <typeparam name="TQueryResult">That query handler's TResult.</typeparam>
public abstract class DecoratorPipelineOrderTestsBase<TCommand, TCommandResult, TQuery, TQueryResult>
{
    /// <summary>
    /// Registers the decorator dependencies' test doubles, one concrete handler per type-parameter
    /// pair, and the repo's real registration sequence (module scans first,
    /// <c>AddApplicationDecorators()</c> last).
    /// </summary>
    /// <param name="services">The service collection under construction.</param>
    protected abstract void ConfigureServices(IServiceCollection services);

    /// <summary>The expected command decorator nesting, outermost first (ADR-014).</summary>
    protected virtual IReadOnlyList<string> ExpectedCommandDecorators =>
    [
        "FeatureGateCommandDecorator",
        "LoggingCommandDecorator",
        "CachingCommandDecorator",
        "ValidatingCommandDecorator",
        "TransactionalCommandDecorator",
    ];

    /// <summary>The expected query decorator nesting, outermost first (ADR-014).</summary>
    protected virtual IReadOnlyList<string> ExpectedQueryDecorators =>
    [
        "FeatureGateQueryDecorator",
        "LoggingQueryDecorator",
        "CachingQueryDecorator",
    ];

    [Fact]
    public void CommandPipeline_NestsDecorators_InAdr014Order() =>
        AssertPipeline(typeof(ICommandHandler<TCommand, TCommandResult>), ExpectedCommandDecorators);

    [Fact]
    public void QueryPipeline_NestsDecorators_InAdr014Order() =>
        AssertPipeline(typeof(IQueryHandler<TQuery, TQueryResult>), ExpectedQueryDecorators);

    private void AssertPipeline(Type handlerServiceType, IReadOnlyList<string> expectedDecorators)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var outermost = scope.ServiceProvider.GetService(handlerServiceType);
        outermost.Should().NotBeNull(
            because: $"ConfigureServices must register a concrete handler for {handlerServiceType.Name} and run the repo's registration sequence");

        var chainNames = UnwrapChain(outermost, handlerServiceType).Select(SimpleTypeName).ToList();

        chainNames.Take(chainNames.Count - 1).Should().Equal(expectedDecorators,
            because: "the resolved handler must be nested in exactly the ADR-014 decorator order, outermost first — TryDecorate applies decorators in reverse registration order, so a reordered AddApplicationDecorators() or a late module scan changes runtime behavior");

        chainNames[^1].Should().NotEndWith("Decorator",
            because: "the innermost element of the pipeline must be the concrete handler");
    }

    /// <summary>
    /// Walks outermost → innermost by reading each decorator's (compiler-generated) private field
    /// holding the inner handler, i.e. the field whose value implements the same closed handler
    /// interface.
    /// </summary>
    private static List<object> UnwrapChain(object outermost, Type handlerServiceType)
    {
        var chain = new List<object> { outermost };
        var current = outermost;

        while (true)
        {
            var inner = current.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.GetValue(current))
                .FirstOrDefault(v => v is not null && handlerServiceType.IsInstanceOfType(v) && !ReferenceEquals(v, current));

            if (inner is null)
            {
                return chain;
            }

            chain.Add(inner);
            current = inner;
        }
    }

    private static string SimpleTypeName(object instance)
    {
        var name = instance.GetType().Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? name[..tick] : name;
    }
}
