using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.ProcessManagers;
using MMCA.Common.Application.UseCases.Contracts;
using Moq;

namespace MMCA.Common.Application.Tests.ProcessManagers;

/// <summary>
/// Tests for <c>AddProcessManagers()</c> (design item 36, rules 17 and 18): it adds the scoped timeout
/// scheduler and a fallback TimeProvider with TryAdd semantics, registers no handler (so it is safe
/// inside or outside the sealed application pipeline), and nothing of it is registered by AddApplication.
/// </summary>
public sealed class AddProcessManagersTests
{
    [Fact]
    public void RegistersSchedulerScoped()
    {
        var services = new ServiceCollection();

        services.AddProcessManagers();

        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(IProcessTimeoutScheduler)).Which;
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be<ProcessTimeoutScheduler>();
    }

    [Fact]
    public void RegistersSystemTimeProviderAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddProcessManagers();

        var descriptor = services.Should().ContainSingle(d => d.ServiceType == typeof(TimeProvider)).Which;
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationInstance.Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void KeepsAnExistingTimeProvider()
    {
        var services = new ServiceCollection();
        var fake = new FakeTimeProvider();
        services.AddSingleton<TimeProvider>(fake);

        services.AddProcessManagers();

        services.Should().ContainSingle(d => d.ServiceType == typeof(TimeProvider))
            .Which.ImplementationInstance.Should().BeSameAs(fake, "TryAddSingleton never replaces the host's clock");
    }

    [Fact]
    public void Idempotent()
    {
        var services = new ServiceCollection();

        services.AddProcessManagers();
        var countAfterFirstCall = services.Count;
        services.AddProcessManagers();

        services.Count.Should().Be(countAfterFirstCall, "a second call adds nothing");
        services.Should().ContainSingle(d => d.ServiceType == typeof(IProcessTimeoutScheduler));
        services.Should().ContainSingle(d => d.ServiceType == typeof(TimeProvider));
    }

    [Fact]
    public void RegistersNoCommandHandler()
    {
        var services = new ServiceCollection();

        services.AddProcessManagers();

        services.Should().NotContain(
            d => d.ServiceType.IsGenericType
                && (d.ServiceType.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)
                    || d.ServiceType.GetGenericTypeDefinition() == typeof(IDomainEventHandler<>)),
            "a handler registered here would escape the decorator pipeline when called after it is sealed");
        services.Select(d => d.ServiceType).Should().BeEquivalentTo(
            new[] { typeof(IProcessTimeoutScheduler), typeof(TimeProvider) },
            "the call registers the scheduler and its clock and nothing else");
    }

    [Fact]
    public async Task ResolvesTheSchedulerFromAScope()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IInternalCommandScheduler>());
        services.AddProcessManagers();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IProcessTimeoutScheduler>().Should().BeOfType<ProcessTimeoutScheduler>();
    }

    [Fact]
    public void AddApplication_DoesNotRegisterTheScheduler()
    {
        var services = new ServiceCollection();

        services.AddApplication();

        services.Should().NotContain(
            d => d.ServiceType == typeof(IProcessTimeoutScheduler),
            "a consumer that adopts no process manager changes nothing");
    }
}
