using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Pages;

namespace MMCA.Common.UI.Tests.Pages;

/// <summary>
/// O-23 (local test run 7): consumer hosts call
/// <c>app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true)</c>, so a
/// full page load that the session-cookie handler answers with 403 (a signed-in user lacking the
/// permission) is re-executed to <c>/not-found</c>, and <see cref="NotFound"/> prerendered "Page Not
/// Found" under HTTP 403 for several seconds before the client showed Access Denied. The contract
/// pinned here: when the re-executed request's original status is 403, the page renders the Access
/// Denied content of <see cref="Forbidden"/>; a real 404, and an interactive render with no
/// HttpContext at all, still render Page Not Found.
/// <para>
/// The request is supplied the way the static server render exposes it: a cascading
/// <see cref="HttpContext"/> (and the same context through <see cref="IHttpContextAccessor"/>),
/// carrying the <see cref="IStatusCodeReExecuteFeature"/> the status-code-pages middleware sets and
/// the original status code, which re-execution leaves on the response.
/// </para>
/// </summary>
public sealed class NotFoundTests : BunitTestBase
{
    // SharedResource.resx "Page.NotFound.Title" / "Page.Forbidden.Title" / "Page.Forbidden.Message".
    private const string NotFoundTitle = "Page Not Found";
    private const string ForbiddenTitle = "Access Denied";
    private const string ForbiddenMessage =
        "You are signed in, but you do not have permission to view this page.";

    [Fact]
    public void WhenReExecutedForA403_RendersAccessDeniedInsteadOfPageNotFound()
    {
        var cut = RenderReExecutedFrom(StatusCodes.Status403Forbidden);

        cut.Markup.Should().NotContain(
            NotFoundTitle,
            "O-23: a 403 re-executed to /not-found must not tell a signed-in user the page does not exist");
        cut.Markup.Should().Contain(
            ForbiddenTitle,
            "O-23: a 403 re-executed to /not-found must render the Access Denied view");
        cut.Markup.Should().Contain(
            ForbiddenMessage,
            "O-23: a 403 re-executed to /not-found must render the Forbidden message");
    }

    [Fact]
    public void WhenReExecutedForA404_StillRendersPageNotFound()
    {
        var cut = RenderReExecutedFrom(StatusCodes.Status404NotFound);

        cut.Markup.Should().Contain(NotFoundTitle);
        cut.Markup.Should().NotContain(ForbiddenTitle);
    }

    [Fact]
    public void WithNoHttpContext_StillRendersPageNotFound()
    {
        // Interactive render (or a direct navigation to /not-found): there is no request to read.
        var cut = RenderAs<NotFound>(TestPrincipal.AuthenticatedUser(), _ => { });

        cut.Markup.Should().Contain(NotFoundTitle);
        cut.Markup.Should().NotContain(ForbiddenTitle);
    }

    private IRenderedComponent<NotFound> RenderReExecutedFrom(int originalStatusCode)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/not-found";

        // Re-execution keeps the original status on the response and records it on the feature.
        context.Response.StatusCode = originalStatusCode;
        context.Features.Set<IStatusCodeReExecuteFeature>(new ReExecuteFeature(originalStatusCode));

        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });

        return RenderAs<NotFound>(
            TestPrincipal.AuthenticatedUser(),
            p => p.AddCascadingValue<HttpContext>(context));
    }

    /// <summary>The feature the status-code-pages middleware sets on a re-executed request.</summary>
    private sealed class ReExecuteFeature(int originalStatusCode) : IStatusCodeReExecuteFeature
    {
        public string OriginalPathBase { get; set; } = string.Empty;

        public string OriginalPath { get; set; } = "/admin/users";

        public string? OriginalQueryString { get; set; }

        public int OriginalStatusCode => originalStatusCode;
    }
}
