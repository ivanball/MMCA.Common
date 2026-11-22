using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Localization;
using MMCA.Common.API.Middleware;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.API.Authorization.MultiFactor;

/// <summary>
/// Wraps the host's <see cref="IAuthorizationMiddlewareResultHandler"/> so a refusal raised by
/// <see cref="MultiFactorRoleAuthorizationHandler"/> answers a coded <c>403</c> Problem Details body
/// (<c>Authorization.MultiFactorRequired</c> in the standard <c>errors</c> extension) instead of the
/// default body-less forbid. Every other result goes to the wrapped handler unchanged.
/// </summary>
/// <remarks>
/// A gRPC request keeps the default forbid, so its framing is not broken by a JSON body.
/// </remarks>
/// <param name="inner">The handler the host registered before the wrapper.</param>
/// <param name="logger">Logger for the refusal line.</param>
internal sealed partial class MultiFactorRefusalResultHandler(
    IAuthorizationMiddlewareResultHandler inner,
    ILogger<MultiFactorRefusalResultHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    private const string ProblemContentType = "application/problem+json";
    private const string Title = "Second factor required";
    private const string Message =
        "This operation requires a second authentication factor. Set up two-factor authentication, or sign in again with your authenticator code.";

    /// <inheritdoc />
    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (!authorizeResult.Forbidden
            || authorizeResult.AuthorizationFailure?.FailureReasons.Any(reason => reason.Handler is MultiFactorRoleAuthorizationHandler) != true
            || IsGrpc(context.Request))
        {
            await inner.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
            return;
        }

        var permission = policy.Requirements.OfType<PermissionRequirement>().FirstOrDefault()?.Permission ?? string.Empty;
        MultiFactorRefused(logger, permission);

        await WriteRefusalAsync(context).ConfigureAwait(false);
    }

    private static bool IsGrpc(HttpRequest request) =>
        request.ContentType?.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task WriteRefusalAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var localizer = services?.GetService<IErrorLocalizer>();
        var error = Error.Forbidden(AuthErrorCodes.MultiFactorRequired, Message, "HasPermission");

        var problemDetails = new ProblemDetails
        {
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
            Title = Title,
            Status = StatusCodes.Status403Forbidden,
            Detail = localizer?.Localize(error.Code, error.Message) ?? error.Message,
        };
        problemDetails.Extensions["errors"] = ErrorHttpMapping.BuildErrorsExtension([error], localizer);

        var response = context.Response;
        response.StatusCode = StatusCodes.Status403Forbidden;
        response.Headers.CacheControl = "no-store";

        if (services?.GetService<IProblemDetailsService>() is { } problemDetailsService
            && await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = problemDetails,
            }).ConfigureAwait(false))
        {
            return;
        }

        await response.WriteAsJsonAsync(problemDetails, options: null, contentType: ProblemContentType, context.RequestAborted)
            .ConfigureAwait(false);
    }

    // The permission name only: no user id and no address.
    [LoggerMessage(Level = LogLevel.Information, Message = "Permission {Permission} refused: the caller's role requires a second factor")]
    private static partial void MultiFactorRefused(ILogger logger, string permission);
}
