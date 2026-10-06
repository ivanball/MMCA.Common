using System.Globalization;
using System.Text.Json;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Broker backpressure gate (rubric section 12). <c>MMCA.Common.Infrastructure.Messaging.MessageBusSettings</c>
/// leaves <c>PrefetchCount</c> and <c>ConcurrentMessageLimit</c> null by default and ignores values of
/// zero or less, so a service whose <c>MessageBus</c> section omits them silently runs on the
/// transport's own defaults. For every service in <see cref="Services"/>, both knobs must be set and
/// positive in its <c>appsettings.json</c>, and the prefetch window must be at least the concurrency
/// it feeds.
/// <para>
/// <b>Subclassing:</b> embed each service's <c>appsettings.json</c> as a manifest resource in the
/// SUBCLASS's test project, with the logical name <c>services.{Service}.appsettings.json</c> (see
/// <see cref="AppSettingsResource"/>), and list the services:
/// </para>
/// <code>
/// &lt;EmbeddedResource Include="..\..\..\Source\Services\MyApp.Catalog.Service\appsettings.json"&gt;
///   &lt;LogicalName&gt;services.Catalog.appsettings.json&lt;/LogicalName&gt;
/// &lt;/EmbeddedResource&gt;
/// </code>
/// <para>
/// The settings are read with <c>System.Text.Json</c> rather than bound to <c>MessageBusSettings</c>,
/// because this package deliberately takes no framework reference (see its csproj): the section and
/// key names are matched case-insensitively, exactly as the configuration binder matches them, and
/// comments and trailing commas are accepted as <c>appsettings.json</c> allows. MMCA.Common's own
/// tests pin those names against <c>MessageBusSettings</c>, so a rename there fails here first.
/// </para>
/// </summary>
public abstract class MessageBusBackpressureTestsBase
{
    /// <summary>The configuration section the broker settings bind from.</summary>
    public const string MessageBusSectionName = "MessageBus";

    /// <summary>The key bounding how many messages one receive endpoint pulls ahead.</summary>
    public const string PrefetchCountKey = "PrefetchCount";

    /// <summary>The key bounding how many messages one receive endpoint processes at once.</summary>
    public const string ConcurrentMessageLimitKey = "ConcurrentMessageLimit";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// The services whose broker backpressure is checked, by the short name used in their embedded
    /// resource (for example <c>["Catalog", "Sales", "Identity"]</c>).
    /// </summary>
    protected abstract IReadOnlyList<string> Services { get; }

    /// <summary>
    /// Assembly the embedded settings are read from. Defaults to the derived type's assembly, so a
    /// subclass gets its OWN test project's resources with no extra wiring.
    /// </summary>
    protected virtual Assembly ResourceAssembly => GetType().Assembly;

    /// <summary>The manifest-resource logical name of one service's <c>appsettings.json</c>.</summary>
    /// <param name="service">A name from <see cref="Services"/>.</param>
    /// <returns>The logical name to read.</returns>
    protected virtual string AppSettingsResource(string service) => $"services.{service}.appsettings.json";

    /// <summary>
    /// Every listed service bounds both its prefetch window and its concurrent consumers with positive
    /// values, and the prefetch window is at least the concurrency it feeds.
    /// </summary>
    [Fact]
    public void EveryServiceAppSettings_BoundsBrokerBackpressure()
    {
        Services.Should().NotBeEmpty(
            because: "at least one service must be listed, so the backpressure gate is not vacuous");

        var violations = new List<string>();
        foreach (var service in Services)
        {
            violations.AddRange(Check(service, ReadEmbedded(AppSettingsResource(service))));
        }

        violations.Should().BeEmpty(
            because: "a service whose MessageBus section omits PrefetchCount or ConcurrentMessageLimit (or sets one to zero or less) silently runs on the transport defaults, and a prefetch window below the concurrency starves its consumers (rubric section 12): "
                + string.Join("; ", violations));
    }

    /// <summary>
    /// The violations one service's settings document carries; empty when both knobs are bounded and
    /// consistent. Public so the parse can be exercised directly over a fixture document.
    /// </summary>
    /// <param name="service">The service name, used in each violation.</param>
    /// <param name="appSettingsJson">The service's <c>appsettings.json</c> text.</param>
    /// <returns>Zero or more violation descriptions.</returns>
    public static IReadOnlyList<string> Check(string service, string appSettingsJson)
    {
        ArgumentNullException.ThrowIfNull(appSettingsJson);

        using var document = JsonDocument.Parse(appSettingsJson, JsonOptions);

        if (!TryGetProperty(document.RootElement, MessageBusSectionName, out var section)
            || section.ValueKind != JsonValueKind.Object)
        {
            return [$"{service} declares no {MessageBusSectionName} section"];
        }

        var prefetch = ReadInt(section, PrefetchCountKey);
        var concurrency = ReadInt(section, ConcurrentMessageLimitKey);

        var violations = new List<string>();
        if (prefetch is not > 0)
        {
            violations.Add($"{service} must set {MessageBusSectionName}:{PrefetchCountKey} to a positive value");
        }

        if (concurrency is not > 0)
        {
            violations.Add($"{service} must set {MessageBusSectionName}:{ConcurrentMessageLimitKey} to a positive value");
        }

        if (prefetch is > 0 && concurrency is > 0 && prefetch < concurrency)
        {
            violations.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{service} prefetches {prefetch} but processes {concurrency} at once; {PrefetchCountKey} must be at least {ConcurrentMessageLimitKey}"));
        }

        return violations;
    }

    private static int? ReadInt(JsonElement section, string key)
    {
        if (!TryGetProperty(section, key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        // The configuration binder reads every value as a string, so a quoted number binds too.
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private string ReadEmbedded(string logicalName)
    {
        using var stream = ResourceAssembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"{logicalName} must be embedded as a resource in {ResourceAssembly.GetName().Name} for the broker backpressure gate to run");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
