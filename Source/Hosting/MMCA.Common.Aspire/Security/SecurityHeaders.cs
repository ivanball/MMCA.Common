using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Aspire.Security;

/// <summary>
/// Strongly-typed configuration for <see cref="SecurityHeadersMiddleware"/>. Defaults match the
/// hardened values each client-facing host previously hand-rolled; override per consumer via the
/// <c>"SecurityHeaders"</c> configuration section or the <c>configure</c> delegate.
/// </summary>
public sealed class SecurityHeadersSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "SecurityHeaders";

    /// <summary>Value of the <c>X-Frame-Options</c> header. Default <c>DENY</c>.</summary>
    public string FrameOptions { get; set; } = "DENY";

    /// <summary>Value of the <c>Referrer-Policy</c> header.</summary>
    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

    /// <summary>Value of the <c>Permissions-Policy</c> header.</summary>
    public string PermissionsPolicy { get; set; } = "geolocation=(), microphone=(), camera=(), payment=()";

    /// <summary>When <see langword="true"/>, emit HSTS outside Development. Default <see langword="true"/>.</summary>
    public bool EnableHsts { get; set; } = true;

    /// <summary>Value of the <c>Strict-Transport-Security</c> header when <see cref="EnableHsts"/> applies.</summary>
    public string HstsValue { get; set; } = "max-age=31536000; includeSubDomains";

    /// <summary>
    /// Static Content-Security-Policy used by the default <see cref="ICspPolicyProvider"/>. Set to
    /// <see langword="null"/>/empty to emit no CSP. The default is a conservative hardened baseline —
    /// <c>default-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'</c>
    /// — safe for the JSON / WebSocket / static responses of API and Gateway hosts. It deliberately omits
    /// <c>script-src</c>/<c>style-src</c> so it does not break an HTML/Blazor host that forgot to register a
    /// provider (Blazor needs <c>script-src 'wasm-unsafe-eval'</c> and MudBlazor needs <c>style-src 'unsafe-inline'</c>);
    /// HTML hosts register their own <see cref="ICspPolicyProvider"/> for a full resource policy.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; } =
        "default-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>When <see langword="true"/> the static CSP is enforced; otherwise it is emitted Report-Only.</summary>
    public bool EnforceContentSecurityPolicy { get; set; } = true;
}

/// <summary>A resolved Content-Security-Policy: its directive string and whether it is enforced.</summary>
/// <param name="Value">The full CSP directive string.</param>
/// <param name="Enforce"><see langword="true"/> to emit <c>Content-Security-Policy</c>; otherwise <c>Content-Security-Policy-Report-Only</c>.</param>
public sealed record CspPolicy(string Value, bool Enforce);

/// <summary>
/// Resolves the Content-Security-Policy for a response. The framework ships a static provider driven
/// by <see cref="SecurityHeadersSettings"/>; a host that needs a dynamic policy (e.g. a Blazor host
/// pinning <c>connect-src</c> to its API origin) registers its own implementation before calling
/// <see cref="SecurityHeadersExtensions.AddCommonSecurityHeaders"/> — the per-consumer CSP allow-list hook.
/// </summary>
public interface ICspPolicyProvider
{
    /// <summary>Returns the CSP to emit for the current response, or <see langword="null"/> to emit none.</summary>
    CspPolicy? GetPolicy(HttpContext context);
}

/// <summary>Default <see cref="ICspPolicyProvider"/>: returns the static CSP configured in <see cref="SecurityHeadersSettings"/>.</summary>
internal sealed class StaticCspPolicyProvider : ICspPolicyProvider
{
    private readonly CspPolicy? _policy;

    public StaticCspPolicyProvider(IOptions<SecurityHeadersSettings> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value.ContentSecurityPolicy;
        _policy = string.IsNullOrWhiteSpace(value)
            ? null
            : new CspPolicy(value, options.Value.EnforceContentSecurityPolicy);
    }

    public CspPolicy? GetPolicy(HttpContext context) => _policy;
}

/// <summary>
/// Adds hardened security response headers to every response: <c>X-Content-Type-Options</c>,
/// <c>X-Frame-Options</c>, <c>Referrer-Policy</c>, <c>Permissions-Policy</c>, HSTS (outside Development),
/// and a Content-Security-Policy resolved from <see cref="ICspPolicyProvider"/>. Centralizes what each
/// client-facing host previously hand-rolled.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ICspPolicyProvider _cspPolicyProvider;
    private readonly SecurityHeadersSettings _settings;
    private readonly bool _enableHsts;

    /// <summary>Creates the middleware.</summary>
    public SecurityHeadersMiddleware(
        RequestDelegate next,
        IOptions<SecurityHeadersSettings> options,
        ICspPolicyProvider cspPolicyProvider,
        IWebHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        _next = next;
        _cspPolicyProvider = cspPolicyProvider;
        _settings = options.Value;
        _enableHsts = options.Value.EnableHsts && !environment.IsDevelopment();
    }

    /// <summary>Sets the security headers, then invokes the rest of the pipeline.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = _settings.FrameOptions;
        headers["Referrer-Policy"] = _settings.ReferrerPolicy;
        headers["Permissions-Policy"] = _settings.PermissionsPolicy;

        if (_enableHsts)
        {
            headers.StrictTransportSecurity = _settings.HstsValue;
        }

        var csp = _cspPolicyProvider.GetPolicy(context);
        if (csp is not null)
        {
            if (csp.Enforce)
            {
                headers.ContentSecurityPolicy = csp.Value;
            }
            else
            {
                headers.ContentSecurityPolicyReportOnly = csp.Value;
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>Registration + pipeline extensions for the common security-headers middleware.</summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class SecurityHeadersExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers <see cref="SecurityHeadersSettings"/> (bound from the <c>"SecurityHeaders"</c> section
        /// when <paramref name="configuration"/> is supplied) and the default static
        /// <see cref="ICspPolicyProvider"/>. Register a custom <see cref="ICspPolicyProvider"/> before calling
        /// this to supply a dynamic policy.
        /// </summary>
        public IServiceCollection AddCommonSecurityHeaders(
            IConfiguration? configuration = null,
            Action<SecurityHeadersSettings>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            var optionsBuilder = services.AddOptions<SecurityHeadersSettings>();
            if (configuration is not null)
            {
                optionsBuilder.Bind(configuration.GetSection(SecurityHeadersSettings.SectionName));
            }

            if (configure is not null)
            {
                optionsBuilder.Configure(configure);
            }

            services.TryAddSingleton<ICspPolicyProvider, StaticCspPolicyProvider>();
            return services;
        }
    }

    extension(IApplicationBuilder app)
    {
        /// <summary>Adds <see cref="SecurityHeadersMiddleware"/> to the request pipeline (call early).</summary>
        public IApplicationBuilder UseCommonSecurityHeaders()
        {
            ArgumentNullException.ThrowIfNull(app);
            return app.UseMiddleware<SecurityHeadersMiddleware>();
        }
    }
}
