using MMCA.Common.Application.UseCases.Markers;

namespace MMCA.Common.Architecture.Tests.CacheInvalidationFixtures;

/// <summary>
/// Compiled cache-invalidation shapes for <c>CacheInvalidationConventionRuleTests</c>: one command per
/// way of satisfying or breaking the convention that an invalidating command names its entity through
/// <c>ICacheInvalidating&lt;TEntity&gt;</c> and lets the framework supply the prefix. These are the
/// only <c>ICacheInvalidating</c> implementers in this assembly, so a fixture map over it sees exactly
/// these.
/// </summary>
internal sealed class CacheFixtureInvoice;

/// <summary>Compliant: the generic marker and no CachePrefix of its own.</summary>
internal sealed record IssueCacheFixtureInvoiceCommand(int InvoiceId) : ICacheInvalidating<CacheFixtureInvoice>;

/// <summary>Violation: a hand-written prefix on the non-generic marker, not allow-listed.</summary>
internal sealed record VoidCacheFixtureInvoiceCommand(int InvoiceId) : ICacheInvalidating
{
    public string CachePrefix => "Billing:Invoices:";
}

/// <summary>Violation: names its entity through the generic marker, then redeclares the prefix anyway.</summary>
internal sealed record ReissueCacheFixtureInvoiceCommand(int InvoiceId) : ICacheInvalidating<CacheFixtureInvoice>
{
    public string CachePrefix => "Billing:Reissued:";
}

/// <summary>A deliberately custom, module-wide prefix: a violation unless the subclass allow-lists it.</summary>
internal sealed record ArchiveCacheFixtureInvoicesCommand(int Year) : ICacheInvalidating
{
    public string CachePrefix => "Billing:";
}

/// <summary>
/// Not concrete, so out of the rule's scope even though it implements the non-generic marker: only a
/// type that can actually be sent through the pipeline carries a prefix at runtime.
/// </summary>
internal abstract record CacheFixtureInvoiceCommandBase : ICacheInvalidating
{
    public abstract string CachePrefix { get; }
}
