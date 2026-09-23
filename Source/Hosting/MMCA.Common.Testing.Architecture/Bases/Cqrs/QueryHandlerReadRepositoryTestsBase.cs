namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Fitness function: a query handler reads, so it takes <c>IUnitOfWork.GetReadRepository</c> and
/// never <c>IUnitOfWork.GetRepository</c>. The write repository hands a query the mutation surface
/// (add, remove, tracked reads) it must not use, and exists only for aggregate roots. The rule reads
/// the IL of every <c>IQueryHandler&lt;,&gt;</c> implementation in the map's Application assemblies,
/// async and lambda bodies included.
/// <para>
/// Adoption in a repo with existing offenders: subclass, run once, and either switch each reported
/// handler to <c>GetReadRepository</c> or move it into <see cref="AllowedHandlers"/> with a comment
/// saying why it needs the write surface.
/// </para>
/// </summary>
public abstract class QueryHandlerReadRepositoryTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Handler type full names or namespace prefixes exempt from the rule. Empty by default.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedHandlers => [];

    [Fact]
    public void QueryHandlers_ShouldUseReadRepositories() =>
        ArchitectureRules.QueryHandlersUseReadRepositories(Map, AllowedHandlers);
}
