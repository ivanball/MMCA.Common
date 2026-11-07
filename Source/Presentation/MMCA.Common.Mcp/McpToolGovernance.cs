using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Authorization;
using MMCA.Common.Shared.Auth;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MMCA.Common.Mcp;

/// <summary>
/// The per-tool governance, applied through the SDK's request filters: <c>tools/list</c> drops every
/// tool the caller could not call, and <c>tools/call</c> refuses a disabled tool, a tool whose
/// permission the caller lacks, and a call over the tool's per-minute limit, each as an error result
/// the model can read rather than an exception.
/// </summary>
internal static class McpToolGovernance
{
    /// <summary>The <c>tools/list</c> filter.</summary>
    /// <param name="next">The SDK's list handler.</param>
    /// <returns>The filtered handler.</returns>
    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> ListTools(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            var services = context.Services ?? context.Server.Services
                ?? throw new InvalidOperationException("The MCP request carries no service provider.");
            var settings = services.GetRequiredService<IOptionsMonitor<McpSettings>>().CurrentValue;

            List<Tool> visible = [];
            foreach (var tool in result.Tools)
            {
                if (IsEnabled(settings, tool.Name)
                    && await IsPermittedAsync(services, context.User, FindPermission(context.Server, null, tool.Name)).ConfigureAwait(false))
                {
                    visible.Add(tool);
                }
            }

            result.Tools = visible;

            // The list now depends on who asked, so no client or intermediary may share it.
            result.CacheScope = CacheScope.Private;
            return result;
        };

    /// <summary>The <c>tools/call</c> filter.</summary>
    /// <param name="next">The SDK's call handler.</param>
    /// <returns>The filtered handler.</returns>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> CallTool(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var name = context.Params?.Name ?? string.Empty;
            var services = context.Services ?? context.Server.Services
                ?? throw new InvalidOperationException("The MCP request carries no service provider.");
            var settings = services.GetRequiredService<IOptionsMonitor<McpSettings>>().CurrentValue;

            if (!IsEnabled(settings, name))
            {
                return Refused("Mcp.ToolUnavailable", $"The tool '{name}' is not available.");
            }

            var permission = FindPermission(context.Server, context.MatchedPrimitive, name);
            if (!await IsPermittedAsync(services, context.User, permission).ConfigureAwait(false))
            {
                return Refused(
                    "Mcp.Forbidden",
                    $"The tool '{name}' needs a bearer token for a user who holds its permission.");
            }

            if (settings.Tools.TryGetValue(name, out var tool)
                && tool.PermitsPerMinute is int permitsPerMinute
                && !services.GetRequiredService<McpToolRateLimiter>().TryAcquire(name, CallerKey(services, context.User), permitsPerMinute))
            {
                return Refused(
                    "Mcp.RateLimited",
                    $"The tool '{name}' allows {permitsPerMinute.ToString(CultureInfo.InvariantCulture)} calls per minute. Wait before calling it again.");
            }

            return await next(context, cancellationToken).ConfigureAwait(false);
        };

    private static bool IsEnabled(McpSettings settings, string toolName) =>
        !settings.Tools.TryGetValue(toolName, out var tool) || tool.Enabled;

    private static string? FindPermission(McpServer server, IMcpServerPrimitive? matched, string toolName)
    {
        var tool = matched as McpServerTool;
        if (tool is null)
        {
            server.ServerOptions.ToolCollection?.TryGetPrimitive(toolName, out tool);
        }

        return tool?.Metadata.OfType<McpToolPermissionAttribute>().FirstOrDefault()?.Permission;
    }

    // The same "perm:" policy [HasPermission] resolves, so a role grant or a direct permission claim
    // admits the caller exactly as it would on the module's HTTP endpoint.
    private static async Task<bool> IsPermittedAsync(IServiceProvider services, ClaimsPrincipal? user, string? permission)
    {
        if (permission is null)
        {
            return true;
        }

        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var authorization = services.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(user, PermissionPolicy.NameFor(permission)).ConfigureAwait(false);
        return result.Succeeded;
    }

    private static string CallerKey(IServiceProvider services, ClaimsPrincipal? user) =>
        user.FindUserIdValue()
        ?? services.GetService<IHttpContextAccessor>()?.HttpContext?.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";

    // The starter-kit shape: a refusal is a tool error result carrying a code, so the model reads
    // it and stops instead of retrying blindly.
    private static CallToolResult Refused(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"{code}: {message}" }],
    };
}
