using Microsoft.AspNetCore.Builder;
using MMCA.Common.API.Startup.Pipeline;

namespace MMCA.Common.ServiceHost;

/// <summary>
/// The MiniProfiler middleware step <c>UseMmcaServiceHostAsync</c> adds to the shared pipeline when
/// <c>ApplicationSettings:UseMiniProfiler</c> is true (and only then, because the middleware needs the
/// services <c>AddMiniProfilerIfEnabled</c> registers under the same flag).
/// </summary>
internal static class ServiceHostProfiling
{
    /// <summary>The step name in the middleware pipeline.</summary>
    internal const string StepName = "MiniProfiler";

    /// <summary>
    /// Inserts the step right after tenant resolution, so <c>HttpContext.User</c> is populated when the
    /// profiler answers its own result routes, and the controllers, the CQRS pipeline and EF Core run
    /// inside the profiled request. Who may read the results is decided by
    /// <c>MiniProfilerOptions.ResultsAuthorize</c> (Development only, as
    /// <c>AddMiniProfilerIfEnabled</c> configures it).
    /// </summary>
    /// <param name="pipeline">The seeded default pipeline.</param>
    internal static void InsertMiddleware(MiddlewarePipelineBuilder pipeline) =>
        pipeline.InsertAfter(
            MiddlewarePipelineStepNames.TenantResolution,
            new MiddlewarePipelineStep(StepName, app => app.UseMiniProfiler()));
}
