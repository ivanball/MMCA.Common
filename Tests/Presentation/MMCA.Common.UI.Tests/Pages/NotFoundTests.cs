using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Pages;
using MMCA.Common.UI.Services.Navigation;

namespace MMCA.Common.UI.Tests.Pages;

/// <summary>
/// O-23 (local test run 7): consumer hosts call
/// <c>app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true)</c>, so a
/// full page load that the session-cookie handler answers with 403 (a signed-in user lacking the
/// permission) is re-executed to <c>/not-found</c>, and <see cref="NotFound"/> prerendered "Page Not
/// Found" under HTTP 403 for several seconds before the client showed Access Denied. The contract
/// pinned here: when the render is a 403 re-execution, the page renders the Access Denied content of
/// <see cref="Forbidden"/>; a real 404, and a head with no request to read (the shared default), still
/// render Page Not Found.
/// <para>
/// The page asks <see cref="IForbiddenReExecutionDetector"/>; the Blazor Web host's request-reading
/// implementation is covered in MMCA.Common.UI.Web.Tests
/// (<c>HttpContextForbiddenReExecutionDetectorTests</c>).
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
        var cut = RenderWithDetector(isForbiddenReExecution: true);

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
    public void WhenNotA403ReExecution_StillRendersPageNotFound()
    {
        // A 404 re-executed on the server: the detector reads the request and answers false.
        var cut = RenderWithDetector(isForbiddenReExecution: false);

        cut.Markup.Should().Contain(NotFoundTitle);
        cut.Markup.Should().NotContain(ForbiddenTitle);
    }

    [Fact]
    public void WithTheSharedDefaultDetector_StillRendersPageNotFound()
    {
        // WebAssembly, MAUI and interactive renders: the shared default (BunitTestBase registers the
        // same one AddUIShared does) has no request to read.
        var cut = RenderAs<NotFound>(TestPrincipal.AuthenticatedUser(), _ => { });

        Services.GetRequiredService<IForbiddenReExecutionDetector>().Should().BeOfType<NoForbiddenReExecutionDetector>();
        cut.Markup.Should().Contain(NotFoundTitle);
        cut.Markup.Should().NotContain(ForbiddenTitle);
    }

    [Fact]
    public void AddUIShared_RegistersTheAlwaysFalseDefault_AsATryAdd()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var defaulted = new ServiceCollection();
        defaulted.AddUIShared(configuration);

        var preRegistered = new ServiceCollection();
        var hostDetector = new StubDetector(isForbiddenReExecution: true);
        preRegistered.AddSingleton<IForbiddenReExecutionDetector>(hostDetector);
        preRegistered.AddUIShared(configuration);

        using var defaultedProvider = defaulted.BuildServiceProvider();
        defaultedProvider.GetRequiredService<IForbiddenReExecutionDetector>()
            .Should().BeOfType<NoForbiddenReExecutionDetector>()
            .Which.IsForbiddenReExecution.Should().BeFalse();

        using var preRegisteredProvider = preRegistered.BuildServiceProvider();
        preRegisteredProvider.GetRequiredService<IForbiddenReExecutionDetector>()
            .Should().BeSameAs(hostDetector, "a detector registered before AddUIShared is kept");
    }

    private IRenderedComponent<NotFound> RenderWithDetector(bool isForbiddenReExecution)
    {
        Services.AddSingleton<IForbiddenReExecutionDetector>(new StubDetector(isForbiddenReExecution));
        return RenderAs<NotFound>(TestPrincipal.AuthenticatedUser(), _ => { });
    }

    private sealed class StubDetector(bool isForbiddenReExecution) : IForbiddenReExecutionDetector
    {
        public bool IsForbiddenReExecution => isForbiddenReExecution;
    }
}
