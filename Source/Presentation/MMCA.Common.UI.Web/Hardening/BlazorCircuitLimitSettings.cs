using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.UI.Web.Hardening;

/// <summary>
/// Configuration for the ceiling on concurrently open Blazor Server circuits, bound from the
/// <c>BlazorCircuitLimits</c> section.
/// </summary>
/// <remarks>
/// Separate from <see cref="UiRateLimitingSettings"/> because it bounds a different resource. The
/// rate limiter bounds how fast requests ARRIVE; a circuit is state that stays resident on the
/// server for as long as the connection lives, so a caller who opens circuits slowly enough to stay
/// inside the window still accumulates them without a ceiling. <c>CircuitOptions</c>'
/// <c>DisconnectedCircuitMaxRetained</c> and <c>DisconnectedCircuitRetentionPeriod</c> do not close
/// this: they bound only circuits that have already DISCONNECTED and are being held for reconnect.
/// </remarks>
public sealed class BlazorCircuitLimitSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static string SectionName => "BlazorCircuitLimits";

    /// <summary>
    /// Gets the maximum number of concurrently ACTIVE circuits this replica will hold. A request
    /// that would open one past the ceiling is refused instead.
    /// </summary>
    /// <remarks>
    /// 200 per replica, derived from the container a Blazor UI host typically runs in: 0.25 vCPU
    /// and 0.5 GiB. Roughly 200 MiB of that half-gibibyte is the runtime, the framework and the
    /// static assets this host also serves, leaving on the order of 300 MiB for circuit state; a
    /// MudBlazor page's server-side render tree costs a few hundred kilobytes up to about a
    /// megabyte, so 200 fits with headroom on the memory side and is already generous on the
    /// 0.25 vCPU side, where render throughput binds first. It sits far above real demand for a host
    /// rendering Interactive Auto, where a returning visitor's session moves to the WebAssembly
    /// runtime after the first render and holds no circuit at all. The number is an abuse ceiling,
    /// not a capacity plan: raise it only together with the container's memory.
    /// </remarks>
    [Range(1, 100_000)]
    public int MaxActiveCircuits { get; init; } = 200;

    /// <summary>
    /// Gets how many DISCONNECTED circuits are retained for reconnect. Defence in depth beside
    /// <see cref="MaxActiveCircuits"/>: tighter than the framework default of 100 because a
    /// retained circuit holds the same state an active one does while serving nobody.
    /// </summary>
    [Range(0, 10_000)]
    public int DisconnectedCircuitMaxRetained { get; init; } = 25;

    /// <summary>
    /// Gets how long, in seconds, a disconnected circuit is retained for reconnect. Tighter than the
    /// framework default of three minutes: a visitor who really did drop off Wi-Fi reconnects within
    /// seconds, and everything beyond that is memory held for someone who is gone. A host whose
    /// audience sits on a flaky shared network (a conference venue, where someone walking between
    /// rooms should come back to the state they left) raises this back towards the framework's 180
    /// and leans on <see cref="DisconnectedCircuitMaxRetained"/> to bound the memory.
    /// </summary>
    [Range(5, 3600)]
    public int DisconnectedCircuitRetentionSeconds { get; init; } = 60;
}
