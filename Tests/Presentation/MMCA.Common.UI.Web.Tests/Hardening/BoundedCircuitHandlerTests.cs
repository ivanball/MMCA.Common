using System.Runtime.CompilerServices;
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
    // Circuit has no public constructor. An uninitialized instance is a distinct reference, which is
    // all the handler needs: it keys its admitted set by reference and reads no member of the circuit
    // (L67). Each opened circuit is held in a local so it is closed by the same reference, exactly as
    // the framework hands the same Circuit to both callbacks.
    private static Circuit NewCircuit() => (Circuit)RuntimeHelpers.GetUninitializedObject(typeof(Circuit));

    [Fact]
    public async Task OnCircuitOpened_UpToTheCeiling_IsAccepted()
    {
        var handler = CreateHandler(maxActiveCircuits: 3);

        for (var i = 0; i < 3; i++)
        {
            await handler.OnCircuitOpenedAsync(NewCircuit(), TestContext.Current.CancellationToken);
        }

        handler.ActiveCircuits.Should().Be(3);
    }

    [Fact]
    public async Task OnCircuitOpened_PastTheCeiling_IsRefusedAndLeaksNoPermit()
    {
        var handler = CreateHandler(maxActiveCircuits: 2);
        await handler.OnCircuitOpenedAsync(NewCircuit(), TestContext.Current.CancellationToken);
        await handler.OnCircuitOpenedAsync(NewCircuit(), TestContext.Current.CancellationToken);

        var refused = async () =>
            await handler.OnCircuitOpenedAsync(NewCircuit(), TestContext.Current.CancellationToken);

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
        var first = NewCircuit();
        await handler.OnCircuitOpenedAsync(first, TestContext.Current.CancellationToken);

        await handler.OnCircuitClosedAsync(first, TestContext.Current.CancellationToken);
        await handler.OnCircuitOpenedAsync(NewCircuit(), TestContext.Current.CancellationToken);

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

        await handler.OnCircuitClosedAsync(NewCircuit(), TestContext.Current.CancellationToken);

        handler.ActiveCircuits.Should().Be(0);
    }

    /// <summary>
    /// L67: the framework closes a refused circuit too, and that close must not release a permit
    /// another live circuit holds, or a flood of refused opens would walk the count down and let
    /// circuits past the ceiling.
    /// </summary>
    [Fact]
    public async Task OnCircuitClosed_ForARefusedCircuit_DoesNotReleaseSomeoneElsesPermit()
    {
        var handler = CreateHandler(maxActiveCircuits: 2);
        await handler.OnCircuitOpenedAsync(NewCircuit(), TestContext.Current.CancellationToken);
        await handler.OnCircuitOpenedAsync(NewCircuit(), TestContext.Current.CancellationToken);
        var refusedCircuit = NewCircuit();
        var openRefused = async () =>
            await handler.OnCircuitOpenedAsync(refusedCircuit, TestContext.Current.CancellationToken);
        await openRefused.Should().ThrowAsync<InvalidOperationException>();

        await handler.OnCircuitClosedAsync(refusedCircuit, TestContext.Current.CancellationToken);

        handler.ActiveCircuits.Should().Be(2, "the refused circuit never held a permit");
        var openAnother = async () =>
            await handler.OnCircuitOpenedAsync(NewCircuit(), TestContext.Current.CancellationToken);
        await openAnother.Should().ThrowAsync<InvalidOperationException>("the ceiling is still full");
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
