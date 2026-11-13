namespace MMCA.Common.Testing.Aspire.AppHostTests;

/// <summary>
/// The outage collection. Parallelization is off because an outage test stops a shared container:
/// nothing else may run against this stack while a dependency is down, and the collection must not
/// overlap the default sample collection's AppHost either.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SampleOutageCollection : ICollectionFixture<SampleOutageAppHostFixture>
{
    /// <summary>The collection name test classes join with <c>[Collection]</c>.</summary>
    public const string Name = "sample-outage";
}
