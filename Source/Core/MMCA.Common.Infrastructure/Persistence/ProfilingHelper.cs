using StackExchange.Profiling;

namespace MMCA.Common.Infrastructure.Persistence;

/// <summary>
/// Shared profiling helpers for EF repository decorators. Wraps operations in MiniProfiler
/// timing steps when profiling is active; no-ops when MiniProfiler is not running.
/// </summary>
internal static class ProfilingHelper
{
    internal static Timing? BeginStep(string className, string methodName) =>
        MiniProfiler.Current?.Step($"MMCA.Common.Infrastructure.{className}: {methodName}");

    internal static int Profile(string className, string methodName, Func<int> func)
    {
        using var step = BeginStep(className, methodName);
        return func();
    }

    internal static async Task ProfileAsync(string className, string methodName, Func<Task> func)
    {
        using var step = BeginStep(className, methodName);
        await func().ConfigureAwait(false);
    }

    internal static async Task<T> ProfileAsync<T>(string className, string methodName, Func<Task<T>> func)
    {
        using var step = BeginStep(className, methodName);
        return await func().ConfigureAwait(false);
    }
}
