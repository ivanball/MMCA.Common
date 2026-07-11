using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Components.Capabilities;
using MMCA.Common.UI.Services.Capabilities;
using MMCA.Common.UI.Services.Capabilities.Fallbacks;

namespace MMCA.Common.UI.Tests.Components.Capabilities;

/// <summary>
/// Covers <see cref="ExternalLink"/> (ADR-042): web heads keep a real new-tab anchor, while
/// an intercepting host (BlazorWebView) routes the click through
/// <see cref="IExternalLinkService.OpenAsync"/> because <c>target="_blank"</c> dead-ends there.
/// </summary>
public sealed class ExternalLinkTests : BunitTestBase
{
    [Fact]
    public void OnWebHosts_RendersPlainNewTabAnchor()
    {
        Services.AddSingleton<IExternalLinkService, NullExternalLinkService>();

        var cut = RenderUnderTest<ExternalLink>(p => p
            .Add(c => c.Href, "https://example.com/talk")
            .AddChildContent("Watch live"));

        var anchor = cut.Find("a");
        anchor.GetAttribute("href").Should().Be("https://example.com/talk");
        anchor.GetAttribute("target").Should().Be("_blank");
        anchor.GetAttribute("rel").Should().Contain("noopener");
        cut.Markup.Should().Contain("Watch live");
    }

    [Fact]
    public async Task OnInterceptingHosts_ClickOpensThroughTheService()
    {
        var fake = new FakeExternalLinkService();
        Services.AddSingleton<IExternalLinkService>(fake);

        var cut = RenderUnderTest<ExternalLink>(p => p
            .Add(c => c.Href, "https://example.com/talk")
            .AddChildContent("Watch live"));

        await cut.Find("a").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        fake.Opened.Should().ContainSingle().Which.Should().Be(new Uri("https://example.com/talk"));
    }

    [Fact]
    public async Task OnInterceptingHosts_InvalidHrefIsIgnored()
    {
        var fake = new FakeExternalLinkService();
        Services.AddSingleton<IExternalLinkService>(fake);

        var cut = RenderUnderTest<ExternalLink>(p => p
            .Add(c => c.Href, "not-an-absolute-url")
            .AddChildContent("Broken"));

        await cut.Find("a").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        fake.Opened.Should().BeEmpty();
    }

    private sealed class FakeExternalLinkService : IExternalLinkService
    {
        public List<Uri> Opened { get; } = [];

        public bool InterceptsLinks => true;

        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Opened.Add(uri);
            return Task.CompletedTask;
        }
    }
}
