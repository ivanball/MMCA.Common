using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MMCA.Common.UI.Common.Settings;

namespace MMCA.Common.UI.Web.ClientConfig;

/// <summary>
/// The framework UI settings sections <c>/client-config</c> forwards from the server head's
/// configuration, so a value set where every production value is set (an environment variable, Key
/// Vault, <c>appsettings.Production.json</c> on the UI container) renders the same after hydration as
/// it did in the prerender, with no second copy in the WebAssembly bundle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only what the settings class declares.</b> Each section is bound to its Common.UI settings type
/// and only that type's settable properties are written, so an unrelated or secret key that happens to
/// sit under the same section name in the server configuration never reaches the anonymous document.
/// Every forwarded value is already rendered on public pages (brand, footer, legal links, which
/// optional sign-in and registration blocks show).
/// </para>
/// <para>
/// <b>Only what the server sets.</b> A section absent from the server configuration is skipped, and
/// within a present section only the properties whose keys are present are written, so the WebAssembly
/// client's own defaults (and any static copy it still ships) hold for everything the server leaves
/// unset.
/// </para>
/// <para>
/// <b>The host's extras win.</b> A host that still adds one of these sections through its
/// <c>ClientConfig</c> callback replaces the forwarded one, so a head that forwards a section itself
/// keeps serving exactly what it served before.
/// </para>
/// </remarks>
internal static class ClientConfigUiSections
{
    /// <summary>The forwarded sections, by configuration section name and settings type.</summary>
    internal static readonly IReadOnlyList<(string SectionName, Type SettingsType)> Forwarded =
    [
        (LayoutSettings.SectionName, typeof(LayoutSettings)),
        (RegistrationSettings.SectionName, typeof(RegistrationSettings)),
        (SignInSettings.SectionName, typeof(SignInSettings)),
        (LegalSettings.SectionName, typeof(LegalSettings)),
    ];

    /// <summary>
    /// Builds each forwarded section the configuration carries, keyed by its camelCased section name,
    /// as a camelCased property-to-value map of the keys present.
    /// </summary>
    /// <param name="configuration">The server head's configuration.</param>
    /// <returns>The sections to write, in declaration order.</returns>
    internal static IEnumerable<KeyValuePair<string, object?>> Build(IConfiguration configuration)
    {
        foreach (var (sectionName, settingsType) in Forwarded)
        {
            var section = configuration.GetSection(sectionName);
            if (!section.Exists())
            {
                continue;
            }

            var bound = section.Get(settingsType);
            if (bound is null)
            {
                continue;
            }

            var values = DeclaredSettableProperties(settingsType)
                .Where(property => section.GetSection(property.Name).Value is not null)
                .ToDictionary(
                    property => JsonNamingPolicy.CamelCase.ConvertName(property.Name),
                    property => property.GetValue(bound),
                    StringComparer.Ordinal);

            if (values.Count > 0)
            {
                yield return new(JsonNamingPolicy.CamelCase.ConvertName(sectionName), values);
            }
        }
    }

    /// <summary>
    /// The public instance properties with a public setter or init accessor: what configuration binding
    /// writes, and so what the WebAssembly client can read back. Computed members such as
    /// <c>LegalSettings.HasFooterLinks</c> are excluded.
    /// </summary>
    /// <param name="settingsType">The settings type.</param>
    /// <returns>The forwardable properties.</returns>
    internal static IEnumerable<PropertyInfo> DeclaredSettableProperties(Type settingsType) =>
        settingsType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0);
}
