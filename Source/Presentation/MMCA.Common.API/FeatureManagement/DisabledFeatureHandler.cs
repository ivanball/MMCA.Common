using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.FeatureManagement.Mvc;

namespace MMCA.Common.API.FeatureManagement;

/// <summary>
/// Returns an RFC 9457 Problem Details 404 response when a <see cref="FeatureGateAttribute"/>-protected
/// endpoint is accessed while its feature flag is disabled. Ensures disabled-feature responses
/// are consistent with the standard <c>ApiControllerBase.HandleFailure</c> format.
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
