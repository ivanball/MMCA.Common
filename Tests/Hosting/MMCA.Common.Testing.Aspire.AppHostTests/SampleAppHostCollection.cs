namespace MMCA.Common.Testing.Aspire.AppHostTests;

/// <summary>
/// One collection, so the AppHost is booted once for every test in this tier rather than once per
/// test class. That is the whole reason the fixture is a COLLECTION fixture: starting an
/// orchestrator is the most expensive thing in any repo per assertion.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SampleAppHostCollection : ICollectionFixture<SampleAppHostFixture>
{
    /// <summary>The collection name test classes join with <c>[Collection]</c>.</summary>
    public const string Name = "sample-apphost";
}
