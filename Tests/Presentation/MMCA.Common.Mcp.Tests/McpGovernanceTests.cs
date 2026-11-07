using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using ModelContextProtocol.Protocol;

namespace MMCA.Common.Mcp.Tests;

/// <summary>
/// The approved governance assertions of spec C (T30), driven through the SDK client against the
/// real /mcp endpoint: per-tool enable, per-tool permission, per-tool rate limit and the off switch.
/// </summary>
public sealed class McpGovernanceTests
{
    private static readonly Dictionary<string, object?> EchoArguments = new(StringComparer.Ordinal) { ["text"] = "hi" };

    [Fact]
    public async Task PublicTool_IsListedAndCallable_WithoutAToken()
    {
        await using var app = await McpTestHost.StartAsync(McpTestHost.Enabled());
        await using var client = await McpTestHost.ConnectAsync(app);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync("public_echo", EchoArguments, cancellationToken: TestContext.Current.CancellationToken);

        tools.Select(tool => tool.Name).Should().Contain(["public_echo", "public_ping"]);
        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be("hi");
    }

    // Spec C assertion 2.
    [Fact]
    public async Task DisabledTool_IsAbsentFromToolsList_AndACallToItReturnsAnError()
    {
        await using var app = await McpTestHost.StartAsync(
            McpTestHost.Enabled(("Mcp:Tools:public_echo:Enabled", "false")));
        await using var client = await McpTestHost.ConnectAsync(app);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync("public_echo", EchoArguments, cancellationToken: TestContext.Current.CancellationToken);

        tools.Select(tool => tool.Name).Should().NotContain("public_echo").And.Contain("public_ping");
        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().StartWith("Mcp.ToolUnavailable");
    }

    // Spec C assertion 3: an anonymous caller (no token) and an authenticated caller without the permission.
    [Theory]
    [InlineData(null)]
    [InlineData("sessions:manage")]
    public async Task PermissionedTool_IsHiddenFromAndRefusedFor_ACallerWithoutThePermission(string? permissions)
    {
        await using var app = await McpTestHost.StartAsync(McpTestHost.Enabled());
        await using var client = await McpTestHost.ConnectAsync(app, permissions);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync("report_totals", cancellationToken: TestContext.Current.CancellationToken);

        tools.Select(tool => tool.Name).Should().NotContain("report_totals").And.Contain("public_echo");
        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().StartWith("Mcp.Forbidden").And.NotContain("42");
    }

    [Fact]
    public async Task PermissionedTool_IsListedAndCallable_ForACallerWithThePermission()
    {
        await using var app = await McpTestHost.StartAsync(McpTestHost.Enabled());
        await using var client = await McpTestHost.ConnectAsync(app, SampleTools.ReportPermission);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync("report_totals", cancellationToken: TestContext.Current.CancellationToken);

        tools.Select(tool => tool.Name).Should().Contain("report_totals");
        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be("totals: 42");
    }

    // Spec C assertion 4.
    [Fact]
    public async Task PerToolLimit_ReturnsAnErrorResult_OnTheCallAfterTheLimit()
    {
        await using var app = await McpTestHost.StartAsync(
            McpTestHost.Enabled(("Mcp:Tools:public_echo:PermitsPerMinute", "2")));
        await using var client = await McpTestHost.ConnectAsync(app);
        var ct = TestContext.Current.CancellationToken;

        var first = await client.CallToolAsync("public_echo", EchoArguments, cancellationToken: ct);
        var second = await client.CallToolAsync("public_echo", EchoArguments, cancellationToken: ct);
        var third = await client.CallToolAsync("public_echo", EchoArguments, cancellationToken: ct);
        var otherTool = await client.CallToolAsync("public_ping", cancellationToken: ct);

        first.IsError.Should().NotBe(true);
        second.IsError.Should().NotBe(true);
        third.IsError.Should().BeTrue();
        third.Content.OfType<TextContentBlock>().Single().Text.Should().StartWith("Mcp.RateLimited");
        otherTool.IsError.Should().NotBe(true, "the limit is per tool");
    }

    // Spec C assertion 5.
    [Fact]
    public async Task McpEndpoint_Returns404_WhenMcpIsNotEnabled()
    {
        await using var app = await McpTestHost.StartAsync(
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Mcp:Enabled"] = "false" });
        using var httpClient = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(McpHostingExtensions.DefaultPath, UriKind.Relative))
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
