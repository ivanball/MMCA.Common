using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.FeatureManagement.Mvc;

namespace MMCA.Common.API.FeatureManagement;

/// <summary>
/// Returns an RFC 9457 Problem Details 404 response when a <see cref="FeatureGateAttribute"/>-protected
/// endpoint is accessed while its feature flag is disabled. The body is built here, not by
/// <c>ApiControllerBase.HandleFailure</c>: a fixed title and detail with no <c>errors</c> extension,
/// so every disabled feature answers with the same 404 whichever endpoint it guards.
/// </summary>
public sealed class DisabledFeatureHandler : IDisabledFeaturesHandler
{
    /// <inheritdoc />
    public Task HandleDisabledFeatures(IEnumerable<string> features, ActionExecutingContext context)
    {
        context.Result = new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Feature not available",
            Detail = "The requested feature is not currently available.",
        })
        {
            StatusCode = StatusCodes.Status404NotFound,
        };

        return Task.CompletedTask;
    }
}
