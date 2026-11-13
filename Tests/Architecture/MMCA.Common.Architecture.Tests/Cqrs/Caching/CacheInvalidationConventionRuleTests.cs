using MMCA.Common.Architecture.Tests.CacheInvalidationFixtures;
using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Cqrs.Caching;

/// <summary>
/// Self-test for the cache-invalidation convention shipped in <c>MMCA.Common.Testing.Architecture</c>
/// (<see cref="CacheInvalidationConventionTestsBase"/>). It points private probe subclasses at THIS
/// assembly, whose <c>CacheInvalidationFixtures</c> compile every shape, and pins each behaviour: a
/// generic implementer that declares no prefix passes, a non-generic implementer fails by name, a
/// generic implementer that redeclares <c>CachePrefix</c> fails by name, and the allow-list exempts
/// exactly the types it names. The base's single fact is found and run by reflection, so these tests
/// pin what the rule decides rather than what its method is called. The probes are private so xUnit
/// does not collect their inherited fact as a test of its own.
/// </summary>
public sealed class CacheInvalidationConventionRuleTests
{
    [Fact]
    public void Base_ShipsExactlyOneFact() =>
        BaseFacts().Should().ContainSingle("the convention is one rule: entity-keyed marker, or allow-listed");

    [Fact]
    public void AllowList_IsEmptyByDefault() =>
        new DefaultProbe(new FixtureModuleMap()).DefaultAllowList.Should().BeEmpty(
            "with no override every custom prefix has to be justified");

    [Fact]
    public async Task CompliantGenericCommand_IsNotFlagged()
    {
        var message = await FailureMessageAsync(new Probe(new FixtureModuleMap()));

        message.Should().NotContain(
            nameof(IssueCacheFixtureInvoiceCommand),
            "ICacheInvalidating<TEntity> with no CachePrefix of its own is the convention");
    }

    [Fact]
    public async Task NonGenericImplementer_NotAllowListed_FailsAndIsNamed()
    {
        var message = await FailureMessageAsync(new Probe(new FixtureModuleMap()));

        message.Should().Contain(
            nameof(VoidCacheFixtureInvoiceCommand),
            "a hand-written prefix on the non-generic marker is the drift the rule exists to catch");
    }

    [Fact]
    public async Task GenericImplementer_ThatRedeclaresCachePrefix_FailsAndIsNamed()
    {
        var message = await FailureMessageAsync(new Probe(new FixtureModuleMap()));

        message.Should().Contain(
            nameof(ReissueCacheFixtureInvoiceCommand),
            "naming the entity and then overriding its prefix defeats the point of naming it");
    }

    [Fact]
    public async Task NonGenericImplementer_WithNoAllowList_IsFlaggedByTheDefault()
    {
        var message = await FailureMessageAsync(new DefaultProbe(new FixtureModuleMap()));

        message.Should().Contain(nameof(VoidCacheFixtureInvoiceCommand));
        message.Should().Contain(nameof(ArchiveCacheFixtureInvoicesCommand));
    }

    [Fact]
    public async Task AllowListedType_IsNotFlagged()
    {
        var message = await FailureMessageAsync(
            new Probe(new FixtureModuleMap(), typeof(ArchiveCacheFixtureInvoicesCommand)));

        message.Should().NotContain(
            nameof(ArchiveCacheFixtureInvoicesCommand),
            "an allow-listed type has a deliberately custom prefix");
        message.Should().Contain(
            nameof(VoidCacheFixtureInvoiceCommand),
            "the allow-list exempts exactly what it names and nothing else");
    }

    [Fact]
    public async Task AllowListingEveryViolator_PassesTheRule()
    {
        var failure = await RunRuleAsync(new Probe(
            new FixtureModuleMap(),
            typeof(VoidCacheFixtureInvoiceCommand),
            typeof(ReissueCacheFixtureInvoiceCommand),
            typeof(ArchiveCacheFixtureInvoicesCommand)));

        failure.Should().BeNull(
            "with the three custom-prefix fixtures allow-listed, only the compliant command and the abstract base remain");
    }

    [Fact]
    public async Task AbstractImplementer_IsOutOfScope()
    {
        var message = await FailureMessageAsync(new Probe(new FixtureModuleMap()));

        message.Should().NotContain(
            nameof(CacheFixtureInvoiceCommandBase),
            "the rule covers concrete types only");
    }

    [Fact]
    public async Task FrameworkMappedApplicationAssembly_IsScannedToo()
    {
        var message = await FailureMessageAsync(new Probe(new FixtureFrameworkMap()));

        message.Should().Contain(
            nameof(VoidCacheFixtureInvoiceCommand),
            "every mapped Application assembly is in scope, framework as well as module, or the rule is vacuous in MMCA.Common");
    }

    private static List<MethodInfo> BaseFacts() =>
        [.. typeof(CacheInvalidationConventionTestsBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
        ];

    /// <summary>Runs the base's single fact on <paramref name="suite"/> and returns its failure, or null when it passes.</summary>
    private static async Task<Exception?> RunRuleAsync(CacheInvalidationConventionTestsBase suite)
    {
        var fact = BaseFacts().Should().ContainSingle().Which;

        try
        {
            if (fact.Invoke(suite, null) is Task pending)
            {
                await pending;
            }

            return null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return ex.InnerException;
        }
        catch (Xunit.Sdk.XunitException ex)
        {
            return ex;
        }
    }

    private static async Task<string> FailureMessageAsync(CacheInvalidationConventionTestsBase suite)
    {
        var failure = await RunRuleAsync(suite);

        failure.Should().NotBeNull("the fixtures include commands that break the convention");

        return failure.Message;
    }

    /// <summary>A probe with an explicit allow-list.</summary>
    private sealed class Probe(IArchitectureMap map, params Type[] allowed) : CacheInvalidationConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = map;

        protected override IReadOnlyCollection<Type> AllowedCustomPrefixTypes { get; } = allowed;
    }

    /// <summary>A probe that keeps the base's default allow-list.</summary>
    private sealed class DefaultProbe(IArchitectureMap map) : CacheInvalidationConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = map;

        public IReadOnlyCollection<Type> DefaultAllowList => AllowedCustomPrefixTypes;
    }

    /// <summary>This assembly as a MODULE Application layer, where a consumer's commands live.</summary>
    private sealed class FixtureModuleMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Module("Billing", Layer.Application, typeof(IssueCacheFixtureInvoiceCommand).Assembly),
        ];
    }

    /// <summary>This assembly as a FRAMEWORK Application layer, the shape of MMCA.Common's module-less map.</summary>
    private sealed class FixtureFrameworkMap : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() =>
        [
            Framework(Layer.Application, typeof(IssueCacheFixtureInvoiceCommand).Assembly),
        ];
    }
}
