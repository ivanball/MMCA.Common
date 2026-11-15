using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MMCA.Common.UI.Web.Components.Pages;
using Moq;
using MudBlazor.Services;

namespace MMCA.Common.UI.Web.Tests.Pages;

/// <summary>
/// Renders the SSR <see cref="Error"/> page the way <c>UseExceptionHandler("/Error")</c> re-execution
/// does (static, with the request's <see cref="HttpContext"/> cascaded): the request id shown is the
/// current activity's id when one is running, the request's trace identifier otherwise, and nothing
/// when neither exists. The Development diagnostics appear only in Development.
/// </summary>
public sealed class ErrorPageTests
{
    [Fact]
    public async Task WithACurrentActivity_ShowsTheActivityId()
    {
        Activity.Current = null;
        using var activity = new Activity("request").Start();
        var context = new DefaultHttpContext { TraceIdentifier = "trace-42" };

        var html = await RenderAsync(context, Environments.Production);

        html.Should().Contain($"<code>{activity.Id}</code>").And.NotContain("trace-42");
        html.Should().Contain("Request ID:");
    }

    [Fact]
    public async Task WithoutAnActivity_ShowsTheRequestsTraceIdentifier()
    {
        Activity.Current = null;
        var context = new DefaultHttpContext { TraceIdentifier = "trace-42" };

        var html = await RenderAsync(context, Environments.Production);

        html.Should().Contain("<code>trace-42</code>");
    }

    [Fact]
    public async Task WithNeitherAnActivityNorAContext_ShowsNoRequestId()
    {
        Activity.Current = null;

        var html = await RenderAsync(context: null, Environments.Production);

        html.Should().NotContain("Request ID:").And.NotContain("<code>");
        html.Should().Contain("An error occurred while processing your request.");
    }

    [Fact]
    public async Task WithAnEmptyTraceIdentifier_ShowsNoRequestId()
    {
        Activity.Current = null;
        var context = new DefaultHttpContext { TraceIdentifier = string.Empty };

        var html = await RenderAsync(context, Environments.Production);

        html.Should().NotContain("Request ID:");
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public async Task DevelopmentDiagnostics_AppearOnlyInDevelopment(string environment, bool shown)
    {
        Activity.Current = null;

        var html = await RenderAsync(new DefaultHttpContext { TraceIdentifier = "trace-42" }, environment);

        html.Contains("Development Mode", StringComparison.Ordinal).Should().Be(shown);
    }

    private static async Task<string> RenderAsync(HttpContext? context, string environmentName)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization();
        services.AddMudServices();
        services.AddSingleton(environment.Object);
        services.AddSingleton(new Mock<IJSRuntime>().Object);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment page = builder =>
            {
                builder.OpenComponent<Error>(0);
                builder.CloseComponent();
            };
            var output = await renderer.RenderComponentAsync<CascadingValue<HttpContext?>>(
                ParameterView.FromDictionary(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [nameof(CascadingValue<>.Value)] = context,
                    [nameof(CascadingValue<>.ChildContent)] = page,
                }));
            return output.ToHtmlString();
        });
    }
}
