using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.UI.Services.Auth.Tokens;

/// <summary>
/// Optional WebAssembly boot optimization: pre-hydrates the access token off the boot path.
/// </summary>
/// <remarks>
/// <para>
/// Hydration is a JS interop hop to read the session cookies plus a same-origin token exchange, and
/// without a warm-up it runs INLINE on whichever API call the user makes first, costing that call
/// the whole round trip on a cold start. Start it after <c>builder.Build()</c> and deliberately
/// discard the task (<c>_ = TokenHydrationWarmup.WarmAsync(host.Services);</c>) so it overlaps first
/// render instead of delaying it.
/// </para>
/// <para>
/// <b>Read-only and silent.</b> <see cref="ITokenStorageService.GetAccessTokenAsync"/> is the READ
/// path: it hydrates when there is a session and answers null when there is not, and it never forces
/// a refresh, so an anonymous visitor gets a no-op. Nothing escapes: boot behaves identically
/// whether the warm-up succeeds, fails or finds nothing, and the first real call re-runs the same
/// hydration with its own error handling around it.
/// </para>
/// </remarks>
public static partial class TokenHydrationWarmup
{
    /// <summary>
    /// Reads the access token once through the registered <see cref="ITokenStorageService"/>,
    /// swallowing and debug-logging any failure.
    /// </summary>
    /// <param name="services">
    /// The host's root provider. Scoped services resolve from it on WebAssembly (one app instance,
    /// one scope).
    /// </param>
    /// <returns>A task that completes when the warm-up has finished; it never faults.</returns>
    public static async Task WarmAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Resolved before the try so the handler itself cannot be the thing that throws.
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(TokenHydrationWarmup).FullName!);

        try
        {
            await services.GetRequiredService<ITokenStorageService>().GetAccessTokenAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A warm-up is a latency optimization: any failure is swallowed by design, the first real call hydrates again with its own handling.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Debug, not Warning: on the anonymous path a failure here is expected and uninteresting.
            if (logger is not null)
            {
                LogWarmupFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Access-token warm-up failed; the first authenticated call will hydrate instead.")]
    private static partial void LogWarmupFailed(ILogger logger, Exception exception);
}
