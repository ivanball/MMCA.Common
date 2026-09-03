namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// CQRS handler fitness functions: handlers and validators live only in Application; handlers and other
/// application services don't broker other handlers; and no <c>*Service</c> exceeds the god-class
/// constructor-arity ceiling (override <see cref="MaxServiceConstructorParameters"/> per repo).
/// </summary>
public abstract class HandlerConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    protected virtual int MaxServiceConstructorParameters => 8;

    [Fact]
    public void Handlers_ShouldResideIn_ApplicationLayer() => ArchitectureRules.HandlersResideInApplicationLayer(Map);

    [Fact]
    public void Handlers_ShouldNotInject_OtherHandlers() => ArchitectureRules.HandlersDoNotInjectOtherHandlers(Map);

    [Fact]
    public void ApplicationServices_ShouldNotInject_Handlers() => ArchitectureRules.ApplicationServicesDoNotInjectHandlers(Map);

    [Fact]
    public void ApplicationServices_ShouldNotExceed_ConstructorArity() => ArchitectureRules.ApplicationServicesRespectConstructorArity(Map, MaxServiceConstructorParameters);

    [Fact]
    public void Validators_ShouldResideIn_ApplicationLayer() => ArchitectureRules.ValidatorsResideInApplicationLayer(Map);

    [Fact]
    public void EventHandlers_ShouldResideIn_ApplicationLayer_AndBeSealed() => ArchitectureRules.DomainEventHandlersResideInApplicationAndSealed(Map);
}
