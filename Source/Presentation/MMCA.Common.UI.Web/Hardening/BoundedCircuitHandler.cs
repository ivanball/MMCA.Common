using System.Globalization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MMCA.Common.UI.Web.Hardening;

/// <summary>
/// Caps how many Blazor Server circuits this replica holds open at once, refusing the ones past
/// <see cref="BlazorCircuitLimitSettings.MaxActiveCircuits"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a handler and not CircuitOptions.</b> <c>DisconnectedCircuitMaxRetained</c> bounds
/// circuits that have already dropped their connection; nothing in <c>CircuitOptions</c> bounds
/// circuits that are open and connected. Since every page load on a public Blazor origin opens one
/// (a host rendering Interactive Auto makes the first render a Server circuit), an unauthenticated
/// loop over the page and negotiate endpoints accumulates live render state until the container
/// restarts. The handler is the documented extension point that sees a circuit being opened and
/// closed, so the count is kept here.
/// </para>
/// <para>
/// <b>Why it throws.</b> <see cref="CircuitHandler.OnCircuitOpenedAsync"/> returns
/// <see cref="Task"/> and has no "refuse" return value, so an exception is the only way to stop a
/// circuit from starting. This is one of the few places in the framework where the Result pattern
/// does not apply, because the contract being implemented belongs to ASP.NET Core. The client sees
/// the standard Blazor reconnect UI rather than a crashed page, and the count is decremented before
/// the throw so a refusal never leaks a permit.
/// </para>
/// <para>
/// <b>Registered as a singleton</b> so ONE count spans the replica
/// (<c>AddBoundedBlazorCircuits()</c> does this). Circuit handlers are resolved from each circuit's
/// own scope, so a scoped registration would count to one and cap nothing. The type keeps no
/// per-circuit state: every callback receives its <see cref="Circuit"/>.
/// </para>
/// </remarks>
/// <param name="settings">The bound circuit limits.</param>
/// <param name="logger">Logger for refusal diagnostics.</param>
public sealed partial class BoundedCircuitHandler(
    IOptions<BlazorCircuitLimitSettings> settings,
    ILogger<BoundedCircuitHandler> logger) : CircuitHandler
{
    private int _activeCircuits;

    /// <summary>
    /// Gets the number of circuits currently counted as open on this replica. Public so a consuming
    /// host can assert the ceiling behaves, and so a diagnostic endpoint can report saturation.
    /// </summary>
    public int ActiveCircuits => Volatile.Read(ref _activeCircuits);

    /// <summary>
    /// Runs LAST among the registered handlers on the way in, so a refusal happens after cheaper
    /// handlers have done their work rather than in the middle of it.
    /// </summary>
    public override int Order => int.MaxValue;

    /// <inheritdoc />
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var ceiling = settings.Value.MaxActiveCircuits;

        // Increment first and roll back on refusal: reading then incrementing would let two
        // simultaneous opens both observe the last free slot and both take it.
        var active = Interlocked.Increment(ref _activeCircuits);
        if (active > ceiling)
        {
            Interlocked.Decrement(ref _activeCircuits);
            LogCircuitRefused(logger, ceiling);

            return Task.FromException(new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The site is holding its maximum of {ceiling} interactive sessions on this instance. Please retry in a moment.")));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Floor at zero rather than trusting the pairing: a close without a counted open (a circuit
        // torn down before this handler ran) would otherwise drive the count negative and hand out
        // permits forever.
        var active = Interlocked.Decrement(ref _activeCircuits);
        if (active < 0)
        {
            Interlocked.Exchange(ref _activeCircuits, 0);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refused a new Blazor circuit: this replica already holds its ceiling of {Ceiling} active circuits. Sustained refusals mean either a flood or a site that has outgrown its container.")]
    private static partial void LogCircuitRefused(ILogger logger, int ceiling);
}
