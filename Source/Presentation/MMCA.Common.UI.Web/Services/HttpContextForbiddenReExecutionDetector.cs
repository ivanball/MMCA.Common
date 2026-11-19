using Microsoft.AspNetCore.Http;
using MMCA.Common.UI.Services.Navigation;

namespace MMCA.Common.UI.Web.Services;

/// <summary>
/// The Blazor Web host's <see cref="IForbiddenReExecutionDetector"/>: reads the status of the request
/// being rendered. Status-code-pages re-execution keeps the original status on the response, so a 403
/// re-executed to <c>/not-found</c> is still 403 there, and the not-found page renders the Access
/// Denied view instead of "Page Not Found".
/// <para>
/// During static server rendering <see cref="IHttpContextAccessor"/> returns the request being
/// rendered. With no request in scope the answer is <see langword="false"/>, and an interactive
/// circuit's connection request never carries a 403 (a refused connection opens no circuit), so an
/// interactive render keeps rendering Page Not Found.
/// </para>
/// </summary>
internal sealed class HttpContextForbiddenReExecutionDetector : IForbiddenReExecutionDetector
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>Initializes a new instance of the <see cref="HttpContextForbiddenReExecutionDetector"/> class.</summary>
    /// <param name="httpContextAccessor">The accessor for the request being rendered.</param>
    public HttpContextForbiddenReExecutionDetector(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public bool IsForbiddenReExecution =>
        _httpContextAccessor.HttpContext?.Response.StatusCode == StatusCodes.Status403Forbidden;
}
