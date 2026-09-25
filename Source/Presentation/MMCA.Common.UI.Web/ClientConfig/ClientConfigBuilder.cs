using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.UI.Web.ClientConfig;

/// <summary>
/// Collects the app-specific sections a host adds to the <c>/client-config</c> document on top of
/// the framework's <c>Api</c> section (see <see cref="ClientConfigEndpointExtensions"/>). Built
/// once per request, so a section can read request-scoped services and current configuration.
/// </summary>
/// <remarks>
/// Everything added here is served ANONYMOUSLY to every browser before sign-in. Add only values the
/// public site would render anyway (feature flags for sign-in providers, public contact details),
/// never a secret.
/// </remarks>
public sealed class ClientConfigBuilder
{
    private readonly Dictionary<string, object?> _sections = [with(StringComparer.OrdinalIgnoreCase)];

    internal ClientConfigBuilder(HttpContext httpContext) => HttpContext = httpContext;

    /// <summary>Gets the request being answered.</summary>
    public HttpContext HttpContext { get; }

    /// <summary>Gets the request's service provider.</summary>
    public IServiceProvider Services => HttpContext.RequestServices;

    /// <summary>Gets the host configuration.</summary>
    public IConfiguration Configuration => Services.GetRequiredService<IConfiguration>();

    internal IReadOnlyDictionary<string, object?> Sections => _sections;

    /// <summary>
    /// Adds one top-level section to the document. The value is serialized with the web JSON
    /// defaults (camelCase members), and the WASM client reads it back through configuration binding,
    /// which is case-insensitive.
    /// </summary>
    /// <param name="name">The section name, for example <c>OAuth</c>. <c>Api</c> is reserved.</param>
    /// <param name="value">The section's value, typically an anonymous object.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is blank, is the reserved <c>Api</c> section, or was already added.
    /// </exception>
    public ClientConfigBuilder Add(string name, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (string.Equals(name, ClientConfigEndpointExtensions.ApiSectionName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The '{ClientConfigEndpointExtensions.ApiSectionName}' section is written by the framework and cannot be replaced.",
                nameof(name));
        }

        if (!_sections.TryAdd(name, value))
        {
            throw new ArgumentException($"The client-config section '{name}' was already added.", nameof(name));
        }

        return this;
    }
}
