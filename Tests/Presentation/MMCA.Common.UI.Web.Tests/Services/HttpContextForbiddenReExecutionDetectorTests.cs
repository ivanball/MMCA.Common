using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Services.Navigation;
using MMCA.Common.UI.Web.Hosting;
using MMCA.Common.UI.Web.Services;
using MMCA.Common.UI.Web.Tests.Hosting;

namespace MMCA.Common.UI.Web.Tests.Services;

/// <summary>
/// O-23: the Blazor Web host's <see cref="IForbiddenReExecutionDetector"/> reads the status of the
/// request being rendered. Status-code-pages re-execution keeps the original status on the response,
/// so a 403 re-executed to <c>/not-found</c> answers true and the not-found page renders Access Denied;
/// a 404, and a render with no request in scope (an interactive render), answer false.
/// <c>AddMmcaBlazorWebHost</c> replaces the shared always-false default with it.
/// </summary>
public sealed class HttpContextForbiddenReExecutionDetectorTests
{
    [Fact]
    public void WhenTheRequestIsA403ReExecution_AnswersTrue()
    {
        var detector = DetectorOver(ReExecutedFrom(StatusCodes.Status403Forbidden));

        detector.IsForbiddenReExecution.Should().BeTrue("re-execution keeps the original 403 on the response");
    }

    [Fact]
    public void WhenTheRequestIsA404ReExecution_AnswersFalse()
    {
        var detector = DetectorOver(ReExecutedFrom(StatusCodes.Status404NotFound));

        detector.IsForbiddenReExecution.Should().BeFalse();
    }

    [Fact]
    public void WithNoRequestInScope_AnswersFalse()
    {
        var detector = DetectorOver(context: null);

        detector.IsForbiddenReExecution.Should().BeFalse("an interactive render has no request to read");
    }

    [Fact]
    public async Task AddMmcaBlazorWebHost_ReplacesTheSharedDefaultWithTheRequestReadingDetector()
    {
        var builder = BlazorWebHostHarness.CreateBuilder();

        builder.AddMmcaBlazorWebHost(BlazorWebHostHarness.DefaultOptions());
        await using var app = builder.Build();

        app.Services.GetRequiredService<IForbiddenReExecutionDetector>()
            .Should().BeOfType<HttpContextForbiddenReExecutionDetector>();
        builder.Services.Should().ContainSingle(
            d => d.ServiceType == typeof(IForbiddenReExecutionDetector),
            "the host replaces the default rather than registering a second detector");
    }

    private static HttpContextForbiddenReExecutionDetector DetectorOver(HttpContext? context) =>
        new(new HttpContextAccessor { HttpContext = context });

    private static DefaultHttpContext ReExecutedFrom(int originalStatusCode)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/not-found";
        context.Response.StatusCode = originalStatusCode;
        return context;
    }
}
