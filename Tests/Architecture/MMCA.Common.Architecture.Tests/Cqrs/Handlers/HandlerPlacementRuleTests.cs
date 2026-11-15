using MMCA.Common.Architecture.Tests.RuleShapeFixtures;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Cqrs.Handlers;

/// <summary>
/// Adversarial coverage for the handler placement, injection and arity rules in
/// <c>ArchitectureRules.Handlers</c>, the ones Store and ADC run through their architecture tests. Each
/// rule is pointed at THIS assembly, which compiles one shape per way of satisfying or breaking it in
/// <c>RuleShapeFixtures</c>, declared as the layer the rule reads; the failure must name every
/// violating shape and none of the compliant ones. A real framework assembly, or the same assembly
/// declared as a layer the rule does not read, is the passing set.
/// </summary>
public sealed class HandlerPlacementRuleTests
{
    private static readonly Assembly Fixtures = typeof(ShapePlaceOrderHandler).Assembly;

    // -- HandlersResideInApplicationLayer --
    [Fact]
    public void HandlersInModuleDomain_AreFlagged()
    {
        var act = () => ArchitectureRules.HandlersResideInApplicationLayer(Map(Module(Layer.Domain, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"  - {typeof(ShapePlaceOrderHandler).FullName}");
        message.Should().Contain(typeof(ShapeOrderTotalHandler).FullName!, "query handlers are handlers too");
        message.Should().Contain("must reside in the Application layer");
        message.Should().NotContain(nameof(ShapeOrderBrokerService), "a service that injects a handler is not itself a handler");
        message.Should().NotContain(nameof(ShapeOpenNotificationHandler), "a name ending in Handler does not make a type a handler");
    }

    [Fact]
    public void HandlersInFrameworkInfrastructure_AreFlagged()
    {
        var act = () => ArchitectureRules.HandlersResideInApplicationLayer(Map(Framework(Layer.Infrastructure, Fixtures)));

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain(nameof(ShapePlaceOrderHandler), "Infrastructure is read for framework and module layers alike");
    }

    [Fact]
    public void HandlersInApplication_OrInFrameworkDomain_Pass()
    {
        var act = () => ArchitectureRules.HandlersResideInApplicationLayer(Map(
            Module(Layer.Application, Fixtures),
            Framework(Layer.Domain, Fixtures),
            Module(Layer.Domain, typeof(Common.Domain.Entities.BaseEntity<>).Assembly)));

        act.Should().NotThrow("Application is where handlers belong, the framework Domain is outside the rule, and the real Domain holds none");
    }

    // -- HandlersDoNotInjectOtherHandlers --
    [Fact]
    public void HandlerInjectingAHandler_ByInterfaceOrConcreteType_IsFlagged()
    {
        var act = () => ArchitectureRules.HandlersDoNotInjectOtherHandlers(Map(Module(Layer.Application, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain(typeof(ShapeComposingHandler).FullName!, "the injected parameter is a handler interface");
        message.Should().Contain(typeof(ShapeConcreteComposingHandler).FullName!, "the injected parameter is a concrete handler");
        message.Should().Contain("compose at the controller/orchestrator level");
        message.Should().NotContain(nameof(ShapePlaceOrderHandler), "a handler with no handler dependency is fine");
        message.Should().NotContain(nameof(ShapeOrderBrokerService), "only handlers are read by this rule");
        message.Should().NotContain(nameof(ShapeOrderRelay), "only handlers are read by this rule");
    }

    [Fact]
    public void HandlerInjection_IgnoresFrameworkApplication()
    {
        var act = () => ArchitectureRules.HandlersDoNotInjectOtherHandlers(Map(Framework(Layer.Application, Fixtures)));

        act.Should().NotThrow("the rule reads module Application layers only");
    }

    // -- ApplicationServicesDoNotInjectHandlers --
    [Fact]
    public void ServiceInjectingAHandler_ByInterfaceOrConcreteType_IsFlagged()
    {
        var act = () => ArchitectureRules.ApplicationServicesDoNotInjectHandlers(Map(Module(Layer.Application, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain(typeof(ShapeOrderBrokerService).FullName!);
        message.Should().Contain(typeof(ShapeOrderRelay).FullName!);
        message.Should().Contain("dispatch them from the API/controller layer");
        message.Should().NotContain(nameof(ShapeComposingHandler), "a handler injecting a handler is the other rule's concern");
        message.Should().NotContain(nameof(ShapeOrderPricingService), "a service with no handler dependency is fine");
    }

    [Fact]
    public void ServiceInjection_IgnoresFrameworkApplication()
    {
        var act = () => ArchitectureRules.ApplicationServicesDoNotInjectHandlers(Map(Framework(Layer.Application, Fixtures)));

        act.Should().NotThrow("the rule reads module Application layers only");
    }

    // -- ApplicationServicesRespectConstructorArity --
    [Fact]
    public void ServiceAboveTheCeiling_IsFlaggedWithItsWidestConstructor()
    {
        var act = () => ArchitectureRules.ApplicationServicesRespectConstructorArity(
            Map(Module(Layer.Application, Fixtures)), maxParameters: 2);

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"{typeof(ShapeOrderWideService).FullName}: 3 constructor parameters (max 2)");
        message.Should().Contain(
            $"{typeof(ShapeOrderOverloadedService).FullName}: 3 constructor parameters (max 2)",
            "the widest public constructor is the one measured");
        message.Should().Contain("must not exceed 2 constructor dependencies");
        message.Should().NotContain(nameof(ShapeOrderPricingService), "exactly at the ceiling is allowed");
        message.Should().NotContain(nameof(ShapeOrderHiddenService), "a service with no public constructor has nothing to measure");
        message.Should().NotContain(nameof(ShapeOrderWideHelper), "only *Service types are read");
    }

    [Fact]
    public void ServiceArity_AtTheDefaultCeiling_PassesTheFrameworkServices()
    {
        var act = () => ArchitectureRules.ApplicationServicesRespectConstructorArity(Map(
            Module(Layer.Application, typeof(Common.Application.Services.DomainEventDispatcher).Assembly)));

        act.Should().NotThrow("no framework Application service takes more than the default eight dependencies");
    }

    [Fact]
    public void ServiceArity_IgnoresFrameworkApplication()
    {
        var act = () => ArchitectureRules.ApplicationServicesRespectConstructorArity(
            Map(Framework(Layer.Application, Fixtures)), maxParameters: 0);

        act.Should().NotThrow("the rule reads module Application layers only");
    }

    // -- ValidatorsResideInApplicationLayer --
    [Fact]
    public void ValidatorsInModuleDomain_OrFrameworkInfrastructure_AreFlagged()
    {
        var inDomain = () => ArchitectureRules.ValidatorsResideInApplicationLayer(Map(Module(Layer.Domain, Fixtures)));
        var inInfrastructure = () => ArchitectureRules.ValidatorsResideInApplicationLayer(Map(Framework(Layer.Infrastructure, Fixtures)));

        var message = inDomain.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"  - {typeof(ShapeOrderValidator).FullName}");
        message.Should().Contain("validators must reside in the Application layer");
        message.Should().NotContain(nameof(ShapePlaceOrderHandler), "only AbstractValidator subclasses are read");
        inInfrastructure.Should().Throw<XunitException>().Which.Message.Should().Contain(nameof(ShapeOrderRules));
    }

    [Fact]
    public void ValidatorsInApplication_OrFrameworkDomain_Pass()
    {
        var act = () => ArchitectureRules.ValidatorsResideInApplicationLayer(Map(
            Module(Layer.Application, Fixtures),
            Framework(Layer.Domain, Fixtures)));

        act.Should().NotThrow("Application is where validators belong and the framework Domain is outside the rule");
    }

    // -- DomainEventHandlersResideInApplicationAndSealed --
    [Fact]
    public void EventHandlersInModuleDomain_AreFlaggedAsMisplaced()
    {
        var act = () => ArchitectureRules.DomainEventHandlersResideInApplicationAndSealed(Map(Module(Layer.Domain, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"{typeof(ShapeOrderPlacedHandler).FullName} (event handler must not be in Domain)");
        message.Should().Contain(
            $"{typeof(ShapeCleanEventConsumer).FullName} (event handler must not be in Domain)",
            "integration event handlers are held to the same placement");
        message.Should().NotContain("(event handler must be sealed)", "no Application layer is mapped");
        message.Should().NotContain(nameof(ShapePlaceOrderHandler), "a command handler is not an event handler");
    }

    [Fact]
    public void UnsealedEventHandlersInModuleApplication_AreFlagged()
    {
        var act = () => ArchitectureRules.DomainEventHandlersResideInApplicationAndSealed(Map(Module(Layer.Application, Fixtures)));

        var message = act.Should().Throw<XunitException>().Which.Message;
        message.Should().Contain($"{typeof(ShapeUnsealedOrderPlacedHandler).FullName} (event handler must be sealed)");
        message.Should().Contain("must reside in Application and be sealed");
        message.Should().NotContain($"{typeof(ShapeOrderPlacedHandler).FullName} (", "a sealed handler in Application is compliant");
        message.Should().NotContain(nameof(ShapeCleanEventConsumer), "a sealed integration event handler in Application is compliant");
        message.Should().NotContain("(event handler must not be in Domain)", "no Domain layer is mapped");
    }

    [Fact]
    public void EventHandlers_InFrameworkLayers_Pass()
    {
        var act = () => ArchitectureRules.DomainEventHandlersResideInApplicationAndSealed(Map(
            Framework(Layer.Domain, Fixtures),
            Framework(Layer.Application, Fixtures)));

        act.Should().NotThrow("both halves read module layers only");
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
