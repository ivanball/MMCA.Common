using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
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
    /// <see langword="null"/>/empty to emit no CSP. The default is a complete hardened baseline:
    /// <code>
    /// default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
    /// </code>
    /// It ships <c>script-src</c> and <c>style-src</c> at exactly the strength Blazor
    /// (<c>'wasm-unsafe-eval'</c>) and MudBlazor (<c>'unsafe-inline'</c> styles) require, so an HTML host that never registers a
    /// provider still gets a functional policy instead of one silently missing both directives, while
    /// the JSON / WebSocket / static responses of API and Gateway hosts are unaffected.
    /// A host needing a stricter or looser policy configures the <c>"SecurityHeaders"</c> section or
    /// registers its own <see cref="ICspPolicyProvider"/>; the <c>{nonce}</c> placeholder (see
    /// <see cref="CspNonce"/>) is the supported path off <c>'unsafe-inline'</c>.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; } =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

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
/// Per-request Content-Security-Policy nonce produced by <see cref="SecurityHeadersMiddleware"/>.
/// </summary>
/// <remarks>
/// A policy that wants a nonce writes the literal token <c>{nonce}</c> as a source-list entry, e.g.
/// <c>script-src 'self' {nonce}</c>. For every request whose resolved policy contains that token the
/// middleware generates a fresh random value, replaces each occurrence with the quoted source-list form
/// <c>'nonce-&lt;value&gt;'</c>, and stashes the raw value under <see cref="ItemKey"/> before the rest of
/// the pipeline runs, so the page render can read it. A host layout stamps it onto its own tags:
/// <code>
/// @inject Microsoft.AspNetCore.Http.IHttpContextAccessor Http
/// &lt;script nonce="@CspNonce.Get(Http.HttpContext!)" src="app.js"&gt;&lt;/script&gt;
/// </code>
/// A policy with no placeholder generates no nonce and stores nothing.
/// </remarks>
public static class CspNonce
{
    /// <summary>Key under which the raw (unquoted, Base64) nonce is stored in <see cref="HttpContext.Items"/>.</summary>
    public const string ItemKey = "MMCA.CspNonce";

    /// <summary>
    /// Returns the nonce generated for the current request, or <see langword="null"/> when the resolved
    /// policy carried no <c>{nonce}</c> placeholder (or the middleware did not run).
    /// </summary>
    /// <param name="context">The current request.</param>
    public static string? Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) ? value as string : null;
    }
}

/// <summary>
/// Adds hardened security response headers to every response: <c>X-Content-Type-Options</c>,
/// <c>X-Frame-Options</c>, <c>Referrer-Policy</c>, <c>Permissions-Policy</c>, HSTS (outside Development),
/// and a Content-Security-Policy resolved from <see cref="ICspPolicyProvider"/>. Centralizes what each
/// client-facing host previously hand-rolled.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    /// <summary>Literal token a policy uses to request a per-request nonce (see <see cref="CspNonce"/>).</summary>
    private const string NoncePlaceholder = "{nonce}";

    /// <summary>Nonce entropy in bytes; 16 bytes (128 bits) is the CSP specification's recommendation.</summary>
    private const int NonceByteCount = 16;

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
            // A policy carrying the {nonce} token gets a fresh value per request. The raw value lands in
            // HttpContext.Items BEFORE the pipeline runs, because the page render is what stamps it onto
            // its script/style tags; the header carries the quoted 'nonce-<value>' source-list form.
            var value = csp.Value;
            if (value.Contains(NoncePlaceholder, StringComparison.Ordinal))
            {
                var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceByteCount));
                context.Items[CspNonce.ItemKey] = nonce;
                value = value.Replace(NoncePlaceholder, $"'nonce-{nonce}'", StringComparison.Ordinal);
            }

            if (csp.Enforce)
            {
                headers.ContentSecurityPolicy = value;
            }
            else
            {
                headers.ContentSecurityPolicyReportOnly = value;
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
