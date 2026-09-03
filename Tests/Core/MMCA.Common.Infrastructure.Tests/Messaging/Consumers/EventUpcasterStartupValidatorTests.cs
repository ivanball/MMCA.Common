using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Services;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Infrastructure.Messaging.Consumers;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Consumers;

/// <summary>
/// Tests for the internal <c>EventUpcasterStartupValidator</c> (ADR-090): the hosted service whose
/// only job is to force <see cref="IEventUpcasterRegistry"/> to be constructed at host start, so a
/// bad registration graph fails the host instead of dead-lettering the first retired-contract
/// message hours later. The type is internal, and this assembly is on the framework's
/// <c>InternalsVisibleTo</c> list, so it is exercised directly AND through the DI surface it is
/// registered on.
/// </summary>
public sealed class EventUpcasterStartupValidatorTests
{
    // ── Sample contracts (TEST assembly only) ──
    public sealed record class ValidatorSampleV1(string Sku) : BaseIntegrationEvent;

    public sealed record class ValidatorSampleV2(string Sku) : BaseIntegrationEvent
    {
        public override int SchemaVersion => 2;
    }

    public sealed record class ValidatorSampleV3(string Sku) : BaseIntegrationEvent
    {
        public override int SchemaVersion => 3;
    }

    private sealed class SampleV1ToV2Upcaster : IEventUpcaster<ValidatorSampleV1, ValidatorSampleV2>
    {
        public ValidatorSampleV2 Upcast(ValidatorSampleV1 integrationEvent) => new(integrationEvent.Sku);
    }

    private sealed class RivalV1ToV3Upcaster : IEventUpcaster<ValidatorSampleV1, ValidatorSampleV3>
    {
        public ValidatorSampleV3 Upcast(ValidatorSampleV1 integrationEvent) => new(integrationEvent.Sku);
    }

    /// <summary>
    /// Builds the same shape <c>AddApplication</c> plus <c>AddInfrastructure</c> produce: the registry
    /// as a singleton over whatever upcasters were registered, and the validator as one
    /// <see cref="IHostedService"/> entry.
    /// </summary>
    private static ServiceProvider BuildProvider(params Type[] upcasterTypes)
    {
        var services = new ServiceCollection();

        foreach (var upcasterType in upcasterTypes)
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IEventUpcaster), upcasterType));
        }

        services.TryAddSingleton<IEventUpcasterRegistry, EventUpcasterRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EventUpcasterStartupValidator>());

        return services.BuildServiceProvider();
    }

    // ── Clean graph: the host starts ──
    [Fact]
    public async Task StartAsync_WithAValidRegistrationGraph_Completes()
    {
        await using ServiceProvider provider = BuildProvider(typeof(SampleV1ToV2Upcaster));
        var validator = provider.GetServices<IHostedService>().Should().ContainSingle().Which;

        Func<Task> act = () => validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        await validator.StopAsync(CancellationToken.None);
    }

    // ── No upcasters at all: the empty registry is still resolved, and costs nothing ──
    [Fact]
    public async Task StartAsync_WithNoUpcastersRegistered_Completes()
    {
        await using ServiceProvider provider = BuildProvider();
        var validator = provider.GetServices<IHostedService>().Should().ContainSingle().Which;

        Func<Task> act = () => validator.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── Bad graph: resolving the hosted service is where the misconfiguration surfaces, naming both
    //    offending upcasters, so the host never reaches a running state ──
    [Fact]
    public void ResolvingTheValidator_WithADuplicateSourceRegistration_ThrowsNamingBothUpcasters()
    {
        using ServiceProvider provider = BuildProvider(typeof(SampleV1ToV2Upcaster), typeof(RivalV1ToV3Upcaster));

        var act = () => provider.GetServices<IHostedService>().ToList();

        var message = act.Should().Throw<InvalidOperationException>().Which.Message;
        message.Should().Contain(nameof(ValidatorSampleV1));
        message.Should().Contain(nameof(SampleV1ToV2Upcaster));
        message.Should().Contain(nameof(RivalV1ToV3Upcaster));
    }

    // ── Constructed directly: the validated registry is the dependency, and Stop is a no-op ──
    [Fact]
    public async Task StartAsync_ConstructedDirectly_ResolvesThroughTheInjectedRegistry()
    {
        var registry = new EventUpcasterRegistry([new SampleV1ToV2Upcaster()]);
        var sut = new EventUpcasterStartupValidator(registry);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        registry.ResolveTerminalType(typeof(ValidatorSampleV1)).Should().Be<ValidatorSampleV2>();
    }
}
