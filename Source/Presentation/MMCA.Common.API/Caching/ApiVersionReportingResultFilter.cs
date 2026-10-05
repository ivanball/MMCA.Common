using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.API.Caching;

/// <summary>
/// Writes the api-version report headers (<c>api-supported-versions</c>,
/// <c>api-deprecated-versions</c>) before an MVC result writes its body, so the output cache stores
/// them with the entry.
/// </summary>
/// <remarks>
/// The versioning library reports from a <c>Response.OnStarting</c> callback. The output cache
/// snapshots the response headers when the body starts, before those callbacks run, so a cache hit
/// replayed the stored headers without the report. Reporting here, ahead of the body, puts the
/// headers in the snapshot; the library's own callback then finds them present and adds nothing.
/// It reports through whatever <see cref="IReportApiVersions"/> the host registered, so a host
/// with <c>ReportApiVersions</c> off still reports nothing.
/// </remarks>
internal sealed class ApiVersionReportingResultFilter : IResultFilter
{
    /// <inheritdoc />
    public void OnResultExecuting(ResultExecutingContext context)
    {
        var httpContext = context.HttpContext;
        if (httpContext.Response.HasStarted)
        {
            return;
        }

        var metadata = httpContext.GetEndpoint()?.Metadata.GetMetadata<ApiVersionMetadata>();
        var reporter = httpContext.RequestServices.GetService<IReportApiVersions>();
        if (metadata is null || reporter is null)
        {
            return;
        }

        reporter.Report(httpContext.Response, metadata.Map(reporter.Mapping));
    }

    /// <inheritdoc />
    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
