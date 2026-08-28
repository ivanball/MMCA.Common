using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.Testing;

/// <summary>
/// Test helpers for lifting the shared HTTP edge rate limiter inside a test host.
/// </summary>
public static class RateLimiterTestExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Replaces the host's global rate limiter with an unlimited partition, so a test suite that
        /// drives dozens of requests through one host in a few seconds is not throttled by the
        /// production budget. Call it from <c>ConfigureTestServices</c> in a test
        /// <c>WebApplicationFactory</c>.
        /// <para>
        /// It runs as a <c>PostConfigure</c>, so it wins over whatever the host registered no matter
        /// when the host's own <c>AddRateLimiter</c> ran. The limiter MIDDLEWARE stays in the pipeline
        /// (the pipeline-order fitness tests still see it); only its global limiter is neutralized, so
        /// per-endpoint named policies a test deliberately exercises are untouched.
        /// </para>
        /// </summary>
        /// <param name="partitionName">
        /// The partition key the no-limiter is registered under. Only ever surfaces in limiter
        /// diagnostics; defaults to <c>"tests"</c>.
        /// </param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection NeutralizeGlobalRateLimiter(string partitionName = "tests")
        {
            services.PostConfigure<RateLimiterOptions>(options =>
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                    _ => RateLimitPartition.GetNoLimiter(partitionName)));

            return services;
        }
    }
}
