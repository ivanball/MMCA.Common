namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Framework-independence fitness functions: Domain and Shared stay free of infrastructure frameworks,
/// Application stays host-agnostic. Override <see cref="ExtraForbiddenDomainDependencies"/> to add
/// repo-specific bans (e.g. Store → "Stripe", ADC → "RabbitMQ").
/// </summary>
public abstract class DomainPurityTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    protected virtual IEnumerable<string> ExtraForbiddenDomainDependencies => [];

    [Fact]
    public void Domain_ShouldBe_FrameworkFree() => ArchitectureRules.DomainIsFrameworkFree(Map, ExtraForbiddenDomainDependencies);

    [Fact]
    public void Shared_ShouldBe_FrameworkFree() => ArchitectureRules.SharedIsFrameworkFree(Map, ExtraForbiddenDomainDependencies);

    [Fact]
    public void Application_ShouldNotDependOn_EntityFrameworkCore() => ArchitectureRules.ApplicationDoesNotDependOnEntityFrameworkCore(Map);

    [Fact]
    public void Application_ShouldNotDependOn_AspNetCore() => ArchitectureRules.ApplicationDoesNotDependOnAspNetCore(Map);
}
