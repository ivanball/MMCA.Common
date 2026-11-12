using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Localization;
using MMCA.Common.API.Middleware;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Refuses a machine client (ADR-140) on an endpoint where a person takes part. A machine client's
/// token carries <see cref="AuthClaimTypes.TokenUse"/> with the value
/// <see cref="AuthClaimTypes.TokenUseMachine"/>; a request that carries it is answered 403 and the
/// action does not run. Every other caller is left alone: whether the caller is signed in, and what
/// the caller may do, stays with <c>[Authorize]</c> and the permission attributes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A machine client holds what its backing role holds. An endpoint that asks
/// only for a signed-in caller (casting a vote, saving a bookmark, checking in) is therefore open to
/// every machine client, whatever its role. This attribute states, on the endpoint, that the caller
/// must be a person.
/// </para>
/// <para>
/// <b>Scope.</b> Declared on a controller it covers every action of that controller, including the
/// ones a permission gates; declare it on single actions when a program must be able to call the
/// others. It adds nothing to the API description.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RefuseMachineClientsAttribute : Attribute, IAuthorizationFilter
{
    /// <summary>The error code carried by the 403 response.</summary>
    public const string ErrorCode = "MachineClients.EndpointRefused";

    /// <inheritdoc />
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.HttpContext.User.HasClaim(AuthClaimTypes.TokenUse, AuthClaimTypes.TokenUseMachine))
        {
            return;
        }

        context.Result = Refusal(context.HttpContext);
    }

    /// <summary>
    /// The 403 response, built the way the rest of the API surface builds a problem response: through
    /// the registered <see cref="ProblemDetailsFactory"/> when there is one, with the error in the
    /// standard <c>errors</c> extension.
    /// </summary>
    private static ObjectResult Refusal(HttpContext httpContext)
    {
        const string title = "Not available to machine clients";
        const string detail = "This endpoint is for people taking part and cannot be called with a machine client's token.";

        var services = httpContext.RequestServices;
        var problemDetails = services?.GetService<ProblemDetailsFactory>()
                ?.CreateProblemDetails(httpContext, StatusCodes.Status403Forbidden, title: title, detail: detail)
            ?? new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = title,
                Detail = detail,
            };

        problemDetails.Extensions["errors"] = ErrorHttpMapping.BuildErrorsExtension(
            [Error.Forbidden(ErrorCode, detail, nameof(RefuseMachineClientsAttribute))],
            services?.GetService<IErrorLocalizer>());

        return new ObjectResult(problemDetails) { StatusCode = StatusCodes.Status403Forbidden };
    }
}
