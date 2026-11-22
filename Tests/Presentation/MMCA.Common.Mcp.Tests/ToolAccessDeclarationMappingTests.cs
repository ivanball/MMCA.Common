using System.ComponentModel;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace MMCA.Common.Mcp.Tests;

/// <summary>
/// <c>MapCommonMcp</c> refuses to map the endpoint while any registered tool carries neither
/// <see cref="McpToolPermissionAttribute"/> nor <see cref="McpToolPublicAttribute"/>, naming the
/// tool, and maps tools that carry either one.
/// </summary>
public sealed class ToolAccessDeclarationMappingTests
{
    [Fact]
    public async Task MapCommonMcp_Throws_NamingAToolTypeMethodWithNoAccessAttribute()
    {
        await using var app = Build(options => options.ToolTypes.Add(typeof(UndeclaredTools)));

        var act = () => app.MapCommonMcp();

        act.Should().Throw<InvalidOperationException>().WithMessage("*undeclared_lookup*");
    }

    [Fact]
    public async Task MapCommonMcp_Throws_NamingEveryRegisteredToolWithNoAccessAttribute()
    {
        var adHoc = McpServerTool.Create(
            () => "ok",
            new McpServerToolCreateOptions { Name = "ad_hoc_tool", Description = "Returns ok.", ReadOnly = true });
        await using var app = Build(
            options =>
            {
                options.ToolTypes.Add(typeof(SampleTools));
                options.ToolTypes.Add(typeof(UndeclaredTools));
            },
            services => services.AddSingleton(adHoc));

        var act = () => app.MapCommonMcp();

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("undeclared_lookup").And.Contain("ad_hoc_tool")
            .And.NotContain("public_echo").And.NotContain("report_totals");
    }

    [Fact]
    public async Task MapCommonMcp_MapsTools_ThatCarryEitherAccessAttribute()
    {
        await using var app = Build(options => options.ToolTypes.Add(typeof(SampleTools)));

        var act = () => app.MapCommonMcp();
        act.Should().NotThrow();

        // And the full host serves them once mapped (the caller holds the permission the permissioned one needs).
        await using var host = await McpTestHost.StartAsync(McpTestHost.Enabled());
        await using var client = await McpTestHost.ConnectAsync(host, SampleTools.ReportPermission);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // public_echo and public_ping carry [McpToolPublic]; report_totals carries [McpToolPermission].
        tools.Select(t => t.Name).Should().Contain(["public_echo", "public_ping", "report_totals"]);
    }

    [Fact]
    public async Task MapCommonMcp_MapsAnAdHocTool_ThatDeclaresPublicInItsMetadata()
    {
        var declared = McpServerTool.Create(
            () => "ok",
            new McpServerToolCreateOptions
            {
                Name = "declared_ad_hoc",
                Description = "Returns ok.",
                ReadOnly = true,
                Metadata = [new McpToolPublicAttribute()],
            });
        await using var app = Build(
            options => options.ToolTypes.Add(typeof(SampleTools)),
            services => services.AddSingleton(declared));

        var act = () => app.MapCommonMcp();

        act.Should().NotThrow();
    }

    /// <summary>
    /// The <see cref="McpTestHost"/> composition up to, but not including, <c>MapCommonMcp</c>, so a
    /// test observes the mapping call itself.
    /// </summary>
    private static WebApplication Build(
        Action<CommonMcpOptions> configureTools,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(McpTestHost.Enabled());
        configureServices?.Invoke(builder.Services);
        builder.AddCommonMcpServer(configureTools);

        var app = builder.Build();
        app.UseRouting();
        return app;
    }
}

/// <summary>A tool type whose one tool declares no access choice.</summary>
[McpServerToolType]
public static class UndeclaredTools
{
    [McpServerTool(Name = "undeclared_lookup", ReadOnly = true)]
    [Description("Looks a value up.")]
    public static string Lookup([Description("The key.")] string key) => key;
}
