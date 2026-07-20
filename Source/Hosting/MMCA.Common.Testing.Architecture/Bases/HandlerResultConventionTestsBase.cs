namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Opt-in fitness function: every concrete command/query handler's <c>TResult</c> must be
/// <c>MMCA.Common.Shared.Abstractions.Result</c> or <c>Result&lt;T&gt;</c> (or a type derived from
/// them). The CQRS interfaces deliberately carry no compile-time constraint on TResult, but the
/// decorator pipeline's short-circuit paths (feature gate, validation) fabricate failures via
/// <c>ResultFailureFactory</c>, which throws <c>InvalidOperationException</c> at runtime for any
/// non-Result TResult — this base turns that deferred constraint into a build-time gate.
/// <para>
/// Subclass it next to your repo's other <c>*Tests</c> architecture classes, supplying the same
/// <see cref="Map"/>. The base is defensive: it first asserts the map's Application assemblies
/// actually contain handlers, so a mis-pinned assembly cannot make the rules pass vacuously.
/// </para>
/// </summary>
public abstract class HandlerResultConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void ApplicationLayers_DeclareAtLeastOneHandler() => ArchitectureRules.ApplicationLayersDeclareHandlers(Map);

    [Fact]
    public void CommandHandlers_Return_ResultTypes() => ArchitectureRules.CommandHandlersReturnResult(Map);

    [Fact]
    public void QueryHandlers_Return_ResultTypes() => ArchitectureRules.QueryHandlersReturnResult(Map);
}
