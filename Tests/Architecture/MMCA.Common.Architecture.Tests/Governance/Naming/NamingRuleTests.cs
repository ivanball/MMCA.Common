using MMCA.Common.Architecture.Tests.RuleShapeFixtures;
using MMCA.Common.Architecture.Tests.RuleShapeFixtures.DomainEvents;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Governance.Naming;

/// <summary>
/// Adversarial coverage for the naming and sealing rules in <c>ArchitectureRules.Naming</c>, which
/// Store and ADC run through their architecture tests. Each rule is pointed at THIS assembly, whose
/// <c>RuleShapeFixtures</c> compile one shape per way of satisfying or breaking it, declared as the
/// layer the rule reads; the failure must name every violating shape and none of the compliant ones,
/// and the same assembly declared as a layer the rule does not read must pass.
/// </summary>
public sealed class NamingRuleTests
{
    private static readonly Assembly Fixtures = typeof(ShapePlaceOrderHandler).Assembly;

    // -- HandlersAreSealedWithHandlerSuffix --
    [Fact]
    public void UnsealedOrMisnamedHandlers_AreFlagged()
    {
        var act = () => ArchitectureRules.HandlersAreSealedWithHandlerSuffix(Map(Module(Layer.Application, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"{typeof(ShapeUnsealedOrderHandler).FullName} (handlers must end in 'Handler' and be sealed)");
        message.Should().Contain($"{typeof(ShapeOrderProcessor).FullName} (handlers must end in 'Handler' and be sealed)");
        message.Should().NotContain(nameof(ShapePlaceOrderHandler), "sealed with the suffix is compliant");
        message.Should().NotContain(nameof(ShapeOpenNotificationHandler), "an unsealed type that is not a handler is not read");
    }

    [Fact]
    public void HandlerSuffix_IgnoresFrameworkApplication()
    {
        var act = () => ArchitectureRules.HandlersAreSealedWithHandlerSuffix(Map(Framework(Layer.Application, Fixtures)));

        act.Should().NotThrow("the rule reads module Application layers only");
    }

    // -- CommandsHaveCommandOrRequestSuffix / QueriesHaveQuerySuffix (AssertMessageSuffix) --
    [Fact]
    public void CommandWithoutAcceptedSuffix_IsFlaggedOnce_NamingItsHandler()
    {
        var act = () => ArchitectureRules.CommandsHaveCommandOrRequestSuffix(Map(Module(Layer.Application, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        var offender = $"  - {typeof(ShapeCancelOrderMessage).FullName} (handled by {nameof(ShapeCancelOrderHandler)})";
        message.Should().Contain(offender);
        message.Split(offender).Should().HaveCount(
            3,
            "a handler serving one message through two interfaces is one violation, reported once in the list (the assertion message repeats the list)");
        message.Should().Contain("command message types must end in 'Command' or 'Request'");
        message.Should().NotContain(nameof(ShapePlaceOrderCommand), "the 'Command' suffix is accepted");
        message.Should().NotContain(nameof(ShapeRenameOrderRequest), "the 'Request' suffix is accepted");
        message.Should().NotContain(nameof(ShapeOrderTotalFetch), "a query is not read by the command rule");
    }

    [Fact]
    public void QueryWithoutQuerySuffix_IsFlagged()
    {
        var act = () => ArchitectureRules.QueriesHaveQuerySuffix(Map(Module(Layer.Application, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"  - {typeof(ShapeOrderTotalFetch).FullName} (handled by {nameof(ShapeOrderFetchHandler)})");
        message.Should().Contain("query message types must end in 'Query'");
        message.Should().NotContain(nameof(ShapeOrderTotalQuery), "the 'Query' suffix is accepted");
        message.Should().NotContain(nameof(ShapeCancelOrderMessage), "a command is not read by the query rule");
    }

    [Fact]
    public void MessageSuffix_IgnoresFrameworkApplication()
    {
        var map = Map(Framework(Layer.Application, Fixtures));

        var commands = () => ArchitectureRules.CommandsHaveCommandOrRequestSuffix(map);
        var queries = () => ArchitectureRules.QueriesHaveQuerySuffix(map);

        commands.Should().NotThrow("the rule reads module Application layers only");
        queries.Should().NotThrow("the rule reads module Application layers only");
    }

    // -- ValidatorsHaveValidatorOrRulesSuffix --
    [Fact]
    public void ValidatorWithoutAcceptedSuffix_IsFlagged()
    {
        var act = () => ArchitectureRules.ValidatorsHaveValidatorOrRulesSuffix(Map(Module(Layer.Application, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"  - {typeof(ShapeOrderChecks).FullName}");
        message.Should().Contain("validators must end in 'Validator' or 'Rules'");
        message.Should().NotContain(nameof(ShapeOrderValidator), "the 'Validator' suffix is accepted");
        message.Should().NotContain(nameof(ShapeOrderRules), "the 'Rules' suffix is accepted");
        message.Should().NotContain(nameof(ShapeOrderPricingService), "only validators are read");
    }

    [Fact]
    public void ValidatorSuffix_IgnoresFrameworkApplication()
    {
        var act = () => ArchitectureRules.ValidatorsHaveValidatorOrRulesSuffix(Map(Framework(Layer.Application, Fixtures)));

        act.Should().NotThrow("the rule reads module Application layers only");
    }

    // -- SharedDtosHaveDtoOrLookupSuffix --
    [Fact]
    public void DtoWithoutAcceptedSuffix_IsFlagged()
    {
        var act = () => ArchitectureRules.SharedDtosHaveDtoOrLookupSuffix(Map(Module(Layer.Shared, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"  - {typeof(ShapeOrderModel).FullName}");
        message.Should().Contain("shared DTO contracts must end in 'DTO' or 'Lookup'");
        message.Should().NotContain(nameof(ShapeOrderDTO), "the 'DTO' suffix is accepted");
        message.Should().NotContain(nameof(ShapeOrderLookup), "the 'Lookup' suffix is accepted");
        message.Should().NotContain(nameof(ShapeOrderSummary), "a type that is not an IBaseDTO is not read");
    }

    [Fact]
    public void DtoSuffix_IgnoresFrameworkShared()
    {
        var act = () => ArchitectureRules.SharedDtosHaveDtoOrLookupSuffix(Map(Framework(Layer.Shared, Fixtures)));

        act.Should().NotThrow("the rule reads module Shared layers only");
    }

    // -- DomainEventsAreSealedInDomainEventsNamespace --
    [Fact]
    public void UnsealedOrMisfiledDomainEvents_AreFlagged()
    {
        var act = () => ArchitectureRules.DomainEventsAreSealedInDomainEventsNamespace(Map(Module(Layer.Domain, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"{typeof(ShapeOrderDrafted).FullName} (domain events must be sealed");
        message.Should().Contain($"{typeof(ShapeOrderMisfiled).FullName} (domain events must be sealed");
        message.Should().Contain("domain events must be sealed and reside in a *.DomainEvents namespace");
        message.Should().NotContain(nameof(ShapeOrderPlaced) + " ", "sealed in a DomainEvents namespace is compliant");
        message.Should().NotContain(nameof(ShapeOrderPlacedHandler), "an event handler is not an event");
    }

    [Fact]
    public void DomainEvents_IgnoreFrameworkDomain()
    {
        var act = () => ArchitectureRules.DomainEventsAreSealedInDomainEventsNamespace(Map(Framework(Layer.Domain, Fixtures)));

        act.Should().NotThrow("the rule reads module Domain layers only");
    }

    // -- InvariantClassesAreStatic --
    [Fact]
    public void InstantiableOrInheritableInvariants_AreFlagged()
    {
        var act = () => ArchitectureRules.InvariantClassesAreStatic(Map(Module(Layer.Domain, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"  - {typeof(ShapeSealedInvariants).FullName}", "sealed alone is not static");
        message.Should().Contain($"  - {typeof(ShapeAbstractInvariants).FullName}", "abstract alone is not static");
        message.Should().Contain("invariant classes must be static (abstract + sealed)");
        message.Should().NotContain(nameof(ShapeOrderInvariants), "a static class is compliant");
        message.Should().NotContain(nameof(IShapeInvariants), "an interface is not a class");
    }

    [Fact]
    public void Invariants_IgnoreFrameworkDomain()
    {
        var act = () => ArchitectureRules.InvariantClassesAreStatic(Map(Framework(Layer.Domain, Fixtures)));

        act.Should().NotThrow("the rule reads module Domain layers only");
    }

    // -- EfConfigurationsAreSealedWithConfigurationSuffix --
    [Fact]
    public void UnsealedOrMisnamedEfConfigurations_AreFlagged()
    {
        var act = () => ArchitectureRules.EfConfigurationsAreSealedWithConfigurationSuffix(Map(Framework(Layer.Infrastructure, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"{typeof(ShapeOpenOrderConfiguration).FullName} (EF configurations must end in 'Configuration' and be sealed)");
        message.Should().Contain($"{typeof(ShapeOrderMapping).FullName} (EF configurations must end in 'Configuration' and be sealed)");
        message.Should().NotContain($"{typeof(ShapeOrderConfiguration).FullName} (", "sealed with the suffix is compliant");
        message.Should().NotContain(nameof(ShapeOpenRouteHelper), "an unsealed type that is no configuration is not read");
    }

    [Fact]
    public void EfConfigurations_InTheFrameworkInfrastructure_Pass()
    {
        var act = () => ArchitectureRules.EfConfigurationsAreSealedWithConfigurationSuffix(Map(
            Module(Layer.Application, Fixtures),
            Framework(Layer.Infrastructure, typeof(Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext).Assembly)));

        act.Should().NotThrow("only Infrastructure is read, and every framework configuration is sealed with the suffix");
    }

    // -- SpecificationsAreSealedWithSpecificationSuffix --
    [Fact]
    public void UnsealedOrMisnamedSpecifications_InApplication_AreFlagged()
    {
        var act = () => ArchitectureRules.SpecificationsAreSealedWithSpecificationSuffix(Map(Framework(Layer.Application, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"{typeof(ShapeOpenOrdersSpecification).FullName} (specifications must end in 'Specification' and be sealed)");
        message.Should().Contain($"{typeof(ShapeActiveOrdersFilter).FullName} (specifications must end in 'Specification' and be sealed)");
        message.Should().NotContain($"{typeof(ShapeActiveOrdersSpecification).FullName} (", "sealed with the suffix is compliant");
        message.Should().NotContain(nameof(ShapeOpenRouteHelper), "an unsealed type that is no specification is not read");
    }

    [Fact]
    public void Specifications_InDomain_AreFlaggedToo()
    {
        var act = () => ArchitectureRules.SpecificationsAreSealedWithSpecificationSuffix(Map(Module(Layer.Domain, Fixtures)));

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(nameof(ShapeActiveOrdersFilter), "Domain and Application are both read");
    }

    [Fact]
    public void Specifications_OutsideDomainAndApplication_Pass()
    {
        var act = () => ArchitectureRules.SpecificationsAreSealedWithSpecificationSuffix(Map(
            Module(Layer.Infrastructure, Fixtures),
            Module(Layer.Shared, Fixtures),
            Framework(Layer.Domain, typeof(Common.Domain.Entities.BaseEntity<>).Assembly)));

        act.Should().NotThrow("only Domain and Application are read, and the framework Domain's specifications are compliant");
    }

    // -- RepositoriesHaveRepositorySuffix --
    [Fact]
    public void RepositoriesWithoutAcceptedSuffix_AreFlagged_ForEachRepositoryContract()
    {
        var act = () => ArchitectureRules.RepositoriesHaveRepositorySuffix(Map(Framework(Layer.Infrastructure, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"  - {typeof(ShapeOrderVault).FullName}", "IRepository implementations are read");
        message.Should().Contain($"  - {typeof(ShapeOrderReader).FullName}", "IReadRepository implementations are read");
        message.Should().Contain($"  - {typeof(ShapeOrderStore).FullName}", "IWriteRepository implementations are read");
        message.Should().Contain("repository implementations must end in 'Repository' (or 'Decorator'");
        message.Should().NotContain(nameof(ShapeOrderRepository), "the 'Repository' suffix is accepted");
        message.Should().NotContain(nameof(ShapeOrderCachingDecorator), "the 'Decorator' suffix is accepted");
        message.Should().NotContain(nameof(ShapeOrderGateway), "a type with no repository contract is not read");
    }

    [Fact]
    public void Repositories_InTheFrameworkInfrastructure_Pass()
    {
        var act = () => ArchitectureRules.RepositoriesHaveRepositorySuffix(Map(
            Module(Layer.Application, Fixtures),
            Framework(Layer.Infrastructure, typeof(Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext).Assembly)));

        act.Should().NotThrow("only Infrastructure is read, and every framework repository carries an accepted suffix");
    }

    private static FixtureMap Map(params LayerRef[] layers) => new(layers);

    private static LayerRef Module(Layer layer, Assembly assembly) =>
        new("Shapes", layer, assembly, $"MMCA.Common.Shapes.{layer}");

    private static LayerRef Framework(Layer layer, Assembly assembly) =>
        new(string.Empty, layer, assembly, $"MMCA.Common.{layer}");

    /// <summary>A map over exactly the layers a test declares.</summary>
    private sealed class FixtureMap(LayerRef[] layers) : ArchitectureMapBase
    {
        public override string RepoToken => "MMCA.Common";

        protected override IEnumerable<LayerRef> DefineLayers() => layers;
    }
}
