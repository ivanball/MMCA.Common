using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Gateway.RateLimiting;

/// <summary>
/// Registers the NAMED rate-limiter policies a YARP route references through its own
/// <c>RateLimiterPolicy</c> property (<c>"RateLimiterPolicy": "auth-tight"</c>). YARP already knows
/// how to attach a named policy to a route; what it cannot do is create one, and hand-writing an
/// <c>AddPolicy</c> block per gateway is exactly the duplication this package exists to remove.
/// <para>
/// These are ADDITIVE to any global limiter the host installs (for example
/// <c>MMCA.Common.Aspire.Gateway.AddGatewayRateLimiting</c>, which assigns
/// <c>RateLimiterOptions.GlobalLimiter</c>). ASP.NET Core evaluates the global limiter and the
/// route's named policy independently, so a request must satisfy both. Nothing here overwrites the
/// global limiter, and the two packages stay independent of each other.
/// </para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class GatewayRoutePolicyExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers one named fixed-window policy per entry in
        /// <see cref="GatewaySettings.RateLimiterPolicies"/>. A blank name is skipped; an
        /// out-of-range value throws here, at registration, rather than at the first throttled
        /// request (ADR-070 fail-fast).
        /// </summary>
        /// <param name="settings">The bound gateway settings.</param>
        /// <returns>The same service collection for chaining.</returns>
        public IServiceCollection AddGatewayRoutePolicies(GatewaySettings settings)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(settings);

            if (settings.RateLimiterPolicies.Count == 0)
            {
                return services;
            }

            foreach (var policy in settings.RateLimiterPolicies.Values)
            {
                Validator.ValidateObject(policy, new ValidationContext(policy), validateAllProperties: true);
            }

            return services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                foreach (var (name, policy) in settings.RateLimiterPolicies)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var captured = policy;
                    options.AddPolicy<string>(name, httpContext => Partition(httpContext, captured));
                }
            });
        }
    }

    /// <summary>
    /// The partition one request falls into under <paramref name="policy"/>: a fixed window keyed by
    /// the policy's partition choice, or no limiter at all when a per-IP policy cannot resolve the
    /// caller's address.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="policy">The named policy's settings.</param>
    /// <returns>The partition this request counts against.</returns>
    /// <remarks>Internal (not private) so the partition-key selection is unit-testable.</remarks>
    internal static RateLimitPartition<string> Partition(HttpContext httpContext, GatewayRoutePolicySettings policy)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(policy);

        var key = policy.PartitionKey(httpContext.Connection.RemoteIpAddress);

        return key is null
            ? RateLimitPartition.GetNoLimiter(GatewayRoutePolicySettings.GlobalPartitionKey)
            : RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                Window = TimeSpan.FromSeconds(policy.WindowSeconds),
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });
    }
}
