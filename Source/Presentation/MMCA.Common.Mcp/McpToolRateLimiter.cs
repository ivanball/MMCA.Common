using System.Globalization;
using System.Threading.RateLimiting;

namespace MMCA.Common.Mcp;

/// <summary>
/// The per-tool limit (<c>Mcp:Tools:&lt;name&gt;:PermitsPerMinute</c>): a one-minute fixed window per
/// tool and caller, in memory per instance. It sits under the endpoint-wide <c>mcp</c> rate-limit
/// policy, which bounds a caller's total requests; this one bounds an expensive tool on its own.
/// </summary>
internal sealed class McpToolRateLimiter : IDisposable
{
    // The partitioned limiter drops partitions that sit idle, so a stream of distinct callers does
    // not grow the set without bound. The permit count is part of the key, so a configuration reload
    // that changes it starts a fresh window rather than keeping the old count.
    private readonly PartitionedRateLimiter<ToolCall> _limiter = PartitionedRateLimiter.Create<ToolCall, string>(
        call => RateLimitPartition.GetFixedWindowLimiter(
            string.Create(CultureInfo.InvariantCulture, $"{call.Tool}\n{call.PermitsPerMinute}\n{call.Caller}"),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = call.PermitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    /// <summary>Takes one permit for <paramref name="caller"/> on <paramref name="tool"/>.</summary>
    /// <param name="tool">The tool name.</param>
    /// <param name="caller">The caller key: user id, else client IP.</param>
    /// <param name="permitsPerMinute">The tool's configured limit.</param>
    /// <returns><see langword="true"/> when the call may proceed.</returns>
    public bool TryAcquire(string tool, string caller, int permitsPerMinute)
    {
        using var lease = _limiter.AttemptAcquire(new ToolCall(tool, caller, permitsPerMinute));
        return lease.IsAcquired;
    }

    /// <inheritdoc />
    public void Dispose() => _limiter.Dispose();

    private readonly record struct ToolCall(string Tool, string Caller, int PermitsPerMinute);
}
