using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;

namespace MMCA.Common.Aspire.Hosting;

/// <summary>
/// The local multi-replica opt-in: reads a replica count from the AppHost's own environment and runs
/// a service at that many replicas behind Aspire's proxy, so the paths that only misbehave across
/// processes (shared-store lockouts, refresh-rotation reuse grace, outbox claims, the migration
/// applock, non-sticky SignalR negotiation) are exercised in the inner loop.
/// <para>
/// Aspire's proxy balances per TCP CONNECTION, not per request, so pooled HTTP/2 connections tend to
/// pin to one replica while browser, WebSocket and SignalR connections do spread. A run proves
/// correctness under concurrency, not capacity.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class MmcaReplicas
{
    /// <summary>
    /// Reads the replica count from an environment variable of the AppHost's own process. Unset,
    /// unparsable (invariant culture, integer only) or a value of 1 or less all return 1, the
    /// single-instance stack.
    /// </summary>
    /// <param name="variableName">The environment variable to read (for example <c>ADC_REPLICAS</c>).</param>
    /// <returns>The requested replica count, never less than 1.</returns>
    public static int FromEnvironment(string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);

        var parsed = int.TryParse(
            Environment.GetEnvironmentVariable(variableName),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var replicas);

        return parsed && replicas > 1 ? replicas : 1;
    }

    extension(IResourceBuilder<DotnetProjectResource> service)
    {
        /// <summary>
        /// Runs the service at <paramref name="replicas"/> replicas. A count of 1 or less changes
        /// nothing, so the call is safe to make unconditionally with the value
        /// <see cref="FromEnvironment"/> returns.
        /// <para>
        /// With <paramref name="distributedRateLimiting"/> the service also gets
        /// <c>RateLimiting__Distributed=true</c>, the production setting for a replicated service:
        /// without it each replica keeps its own in-memory window and a per-user limit multiplies by
        /// the replica count. Pass <see langword="false"/> for the gateway, whose edge limiters stay
        /// per replica, as in production.
        /// </para>
        /// </summary>
        /// <param name="replicas">The replica count; 1 or less is a no-op.</param>
        /// <param name="distributedRateLimiting">Whether to switch the service's rate limiter to the distributed store.</param>
        /// <returns>The same service resource builder, for chaining.</returns>
        public IResourceBuilder<DotnetProjectResource> WithMmcaReplicas(int replicas, bool distributedRateLimiting)
        {
            ArgumentNullException.ThrowIfNull(service);

            if (replicas <= 1)
            {
                return service;
            }

            service.WithReplicas(replicas);

            return distributedRateLimiting
                ? service.WithEnvironment($"{Extensions.RateLimitingSection}__Distributed", "true")
                : service;
        }
    }
}
