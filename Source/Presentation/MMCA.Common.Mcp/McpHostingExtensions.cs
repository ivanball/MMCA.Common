using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Startup;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace MMCA.Common.Mcp;

/// <summary>
/// The two consumer calls that opt a host into the MCP endpoint:
/// <c>builder.AddCommonMcpServer(options =&gt; options.ToolAssemblies.Add(typeof(MyTools).Assembly))</c>
/// and <c>app.MapCommonMcp()</c>. Both do nothing unless <c>Mcp:Enabled</c> is true.
/// </summary>
/// <remarks>
/// <para>
/// Tools follow the starter-kit pattern: a static <c>[McpServerToolType]</c> class in the module's API
/// project, each method <c>[McpServerTool(ReadOnly = true)]</c> with a <c>[Description]</c>, injecting
/// the query handler the HTTP endpoint injects, so the decorator pipeline (authorization, caching,
/// validation) applies to a tool call exactly as to a request.
/// </para>
/// <para>
/// Authentication: the endpoint is anonymous at the HTTP layer, so a public tool needs no token. A
/// tool marked <see cref="McpToolPermissionAttribute"/> is listed and callable only for a caller the
/// host's authentication handler (the bearer handler) signed in with that permission. The host's
/// <c>UseAuthentication</c> must run before the endpoint, as it does for controllers.
/// </para>
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with extension(T) blocks, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class McpHostingExtensions
{
    /// <summary>The route the endpoint is mapped at.</summary>
    public const string DefaultPath = "/mcp";

    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Registers the MCP server (stateless streamable HTTP) with the per-tool governance filters,
        /// when <c>Mcp:Enabled</c> is true. With it false, only the <see cref="McpSettings"/> options
        /// are registered and no MCP service exists.
        /// </summary>
        /// <param name="configure">Selects the tool classes to expose.</param>
        /// <returns>The same builder for chaining.</returns>
        public IHostApplicationBuilder AddCommonMcpServer(Action<CommonMcpOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            var section = builder.Configuration.GetSection(McpSettings.SectionName);
            builder.Services.AddOptions<McpSettings>()
                .Bind(section)
                .Validate(
                    static settings => settings.Tools.Values.All(tool => tool.PermitsPerMinute is null or > 0),
                    "Mcp:Tools:<name>:PermitsPerMinute must be 1 or more when set.")
                .ValidateOnStart();

            if (!section.GetValue<bool>(nameof(McpSettings.Enabled)))
            {
                return builder;
            }

            var options = new CommonMcpOptions();
            configure(options);

            builder.Services.AddHttpContextAccessor();
            builder.Services.TryAddSingleton<McpToolRateLimiter>();
            builder.Services.TryAddSingleton<McpEnabledMarker>();

            var mcp = builder.Services.AddMcpServer()
                .WithHttpTransport(static transport => transport.SessionMode = HttpServerSessionMode.Stateless)
                .WithRequestFilters(static filters =>
                {
                    filters.AddListToolsFilter(McpToolGovernance.ListTools);
                    filters.AddCallToolFilter(McpToolGovernance.CallTool);
                })

                // The IEnumerable<Type> overload, named explicitly: passing the IList<Type> straight
                // through binds WithTools<TTarget>(target), which registers the list as a tool target.
                .WithTools((IEnumerable<Type>)options.ToolTypes);

            foreach (var assembly in options.ToolAssemblies)
            {
                mcp.WithToolsFromAssembly(assembly);
            }

            return builder;
        }
    }

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps the MCP endpoint when <c>Mcp:Enabled</c> is true, after refusing to start if any tool,
        /// parameter or server description hides text (R023). The endpoint allows anonymous callers
        /// (permissioned tools check the caller themselves) and carries the <c>mcp</c> rate-limit
        /// policy from <c>AddCommonRateLimiting</c>. With MCP disabled nothing is mapped, so the path
        /// answers 404.
        /// </summary>
        /// <param name="pattern">The route; <see cref="DefaultPath"/> unless a host needs another.</param>
        /// <returns>The same route builder for chaining.</returns>
        /// <exception cref="InvalidOperationException">
        /// A description hides text, or <c>Mcp:Enabled</c> is true but <c>AddCommonMcpServer</c> was not called.
        /// </exception>
        public IEndpointRouteBuilder MapCommonMcp(string pattern = DefaultPath)
        {
            ArgumentNullException.ThrowIfNull(endpoints);

            var services = endpoints.ServiceProvider;
            if (services.GetService<McpEnabledMarker>() is null)
            {
                if (services.GetService<IConfiguration>()?.GetValue<bool>($"{McpSettings.SectionName}:{nameof(McpSettings.Enabled)}") == true)
                {
                    throw new InvalidOperationException(
                        "Mcp:Enabled is true but the MCP server is not registered. Call builder.AddCommonMcpServer(...) before Build().");
                }

                return endpoints;
            }

            ToolDescriptionInspector.ThrowIfHiddenText(services.GetRequiredService<IOptions<McpServerOptions>>().Value);

            endpoints.MapMcp(pattern)
                .AllowAnonymous()
                .RequireRateLimiting(WebApplicationBuilderExtensions.RateLimitPolicyMcp);

            return endpoints;
        }
    }

    /// <summary>Registered only when MCP is enabled, so <c>MapCommonMcp</c> can tell.</summary>
    private sealed class McpEnabledMarker;
}
