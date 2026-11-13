using MMCA.Common.Application.UseCases.Crud;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Cqrs.Caching;

/// <summary>
/// Runs the shared cache-invalidation convention (<see cref="CacheInvalidationConventionTestsBase"/>)
/// against MMCA.Common's own Application assembly, the same way MMCA.ADC, MMCA.Store and
/// MMCA.Helpdesk subclass it for their module commands.
/// </summary>
public sealed class CommonCacheInvalidationConventionTests : CacheInvalidationConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    /// <summary>
    /// The generic CRUD commands keep a settable <c>CachePrefix</c> on purpose: the generic
    /// controller constructs them itself, and the <see langword="init"/> setter is how a caller opts out of
    /// invalidation with an empty string. That per-instance override is a deliberately custom prefix.
    /// The verb-discriminated update command inherits the same settable prefix from its base.
    /// </summary>
    protected override IReadOnlyCollection<Type> AllowedCustomPrefixTypes =>
    [
        typeof(UpdateEntityCommand<,,>),
        typeof(UpdateEntityCommand<,,,>),
        typeof(DeleteEntityCommand<,>),
    ];
}
