using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Web.Hardening;

namespace MMCA.Common.UI.Web.Tests.Hardening;

/// <summary>
/// Pins the ceiling on concurrently ACTIVE Blazor circuits (SEC-Store-56, extracted from the copies
/// MMCA.ADC and MMCA.Store each carried). The framework's <c>DisconnectedCircuitMaxRetained</c>
/// bounds only circuits that have already disconnected, so without this handler an unauthenticated
/// loop over the negotiate endpoint accumulates live render state on a 0.5 GiB container until it
/// restarts.
/// </summary>
public sealed class BoundedCircuitHandlerTests
{
    // Circuit has no public constructor, and the handler deliberately reads nothing off it: the
    // count is all it keeps. Driving the callbacks with null is therefore honest rather than a
    // shortcut, and it stops compiling by design the day the handler starts reading the circuit.
    private static readonly Circuit NoCircuit = null!;

    [Fact]
    public async Task OnCircuitOpened_UpToTheCeiling_IsAccepted()
    {
        var handler = CreateHandler(maxActiveCircuits: 3);

        for (var i = 0; i < 3; i++)
        {
            await handler.OnCircuitOpenedAsync(NoCircuit, TestContext.Current.CancellationToken);
        }

        handler.ActiveCircuits.Should().Be(3);
    }

    [Fact]
    public async Task OnCircuitOpened_PastTheCeiling_IsRefusedAndLeaksNoPermit()
    {
        var handler = CreateHandler(maxActiveCircuits: 2);
        await handler.OnCircuitOpenedAsync(NoCircuit, TestContext.Current.CancellationToken);
        await handler.OnCircuitOpenedAsync(NoCircuit, TestContext.Current.CancellationToken);

        var refused = async () =>
            await handler.OnCircuitOpenedAsync(NoCircuit, TestContext.Current.CancellationToken);

        await refused.Should().ThrowAsync<InvalidOperationException>(
            "a circuit past the ceiling must not open: the handler contract has no refusal return value");
        handler.ActiveCircuits.Should().Be(
            2,
            "a refusal must roll its own increment back, or the ceiling would ratchet down to zero under a flood");
    }

    [Fact]
    public async Task OnCircuitClosed_ReleasesThePermit()
    {
        var handler = CreateHandler(maxActiveCircuits: 1);
        await handler.OnCircuitOpenedAsync(NoCircuit, TestContext.Current.CancellationToken);

        await handler.OnCircuitClosedAsync(NoCircuit, TestContext.Current.CancellationToken);
        await handler.OnCircuitOpenedAsync(NoCircuit, TestContext.Current.CancellationToken);

        handler.ActiveCircuits.Should().Be(1);
    }

    /// <summary>
    /// A close that was never counted (a circuit torn down before the handler ran) must not drive
    /// the count negative, which would hand out permits without limit afterwards.
    /// </summary>
    [Fact]
    public async Task OnCircuitClosed_WithoutAMatchingOpen_FloorsAtZero()
    {
        var handler = CreateHandler(maxActiveCircuits: 1);

        await handler.OnCircuitClosedAsync(NoCircuit, TestContext.Current.CancellationToken);

        handler.ActiveCircuits.Should().Be(0);
    }

    /// <summary>
    /// The handler is only a control if the host registers it, and it has to be a SINGLETON: a
    /// scoped registration is resolved once per circuit, so every circuit would count to one and the
    /// ceiling would never be reached.
    /// </summary>
    [Fact]
    public void AddBoundedBlazorCircuits_RegistersTheCeilingAsASingleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddBoundedBlazorCircuits();

        using var provider = services.BuildServiceProvider();
        var first = provider.GetServices<CircuitHandler>().OfType<BoundedCircuitHandler>().Single();
        using var scope = provider.CreateScope();
        var fromScope = scope.ServiceProvider.GetServices<CircuitHandler>()
            .OfType<BoundedCircuitHandler>().Single();

        fromScope.Should().BeSameAs(first, "one count must span the replica, not one count per circuit");
    }

    /// <summary>
    /// The retention half reads the SAME section as the ceiling, so the two numbers cannot drift
    /// apart, and it applies both of them: a callback that set only the count would leave a
    /// disconnected circuit resident for the framework's three minutes.
    /// </summary>
    [Fact]
    public void RetentionFrom_AppliesBothRetentionValuesFromTheBoundSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{BlazorCircuitLimitSettings.SectionName}:DisconnectedCircuitMaxRetained"] = "7",
                [$"{BlazorCircuitLimitSettings.SectionName}:DisconnectedCircuitRetentionSeconds"] = "45",
            })
            .Build();
        var options = new CircuitOptions();

        BlazorCircuitLimitExtensions.RetentionFrom(configuration)(options);

        options.DisconnectedCircuitMaxRetained.Should().Be(7);
        options.DisconnectedCircuitRetentionPeriod.Should().Be(TimeSpan.FromSeconds(45));
    }

    private static BoundedCircuitHandler CreateHandler(int maxActiveCircuits) =>
        new(
            Options.Create(new BlazorCircuitLimitSettings { MaxActiveCircuits = maxActiveCircuits }),
            NullLogger<BoundedCircuitHandler>.Instance);
}
