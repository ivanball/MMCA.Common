using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Gcp.Secrets;

namespace MMCA.Common.Gcp.Tests;

/// <summary>
/// Guards the configuration gate on <c>AddGoogleSecretManagerConfiguration</c>: unless both
/// <c>GoogleSecretManager:ProjectId</c> and <c>GoogleSecretManager:NamePrefix</c> are set, the host
/// gains no configuration source, no Google dependency and no tripwire receipt, while a complete
/// gate appends the Secret Manager source.
/// <para>
/// No test here touches the network, for the same reason as the Key Vault and Secrets Manager
/// siblings' tests: the cases that must NOT add a source run against a real
/// <see cref="HostApplicationBuilder"/>, and the case that DOES add one runs against a builder double
/// whose configuration collects sources without loading them, because a real
/// <c>ConfigurationManager</c> loads each source the instant it is added.
/// </para>
/// </summary>
public sealed class GoogleSecretManagerConfigurationExtensionsTests
{
    [Theory]
    [InlineData(null, "adc-prod-")]
    [InlineData("", "adc-prod-")]
    [InlineData("   ", "adc-prod-")]
    [InlineData("mmca-prod", null)]
    [InlineData("mmca-prod", "")]
    [InlineData("mmca-prod", "   ")]
    [InlineData(null, null)]
    public void AddGoogleSecretManagerConfiguration_WithAnIncompleteGate_IsANoOp(string? projectId, string? prefix)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GoogleSecretManager:ProjectId"] = projectId,
            ["GoogleSecretManager:NamePrefix"] = prefix,
        });
        var sourceCountBefore = builder.Configuration.Sources.Count;

        builder.AddGoogleSecretManagerConfiguration();

        builder.Configuration.Sources.Count.Should().Be(
            sourceCountBefore,
            because: "an unconfigured host must gain no configuration source, so local development and tests never reach for Google Cloud at startup");
        builder.Services.Should().NotContain(
            d => d.ImplementationInstance is HostIntegrationMarker,
            because: "a call that added nothing leaves no receipt, so a prefix that appears later still trips the startup check");
    }

    [Fact]
    public void AddGoogleSecretManagerConfiguration_WithACompleteGate_AppendsTheSourceAndTheReceipt()
    {
        var builder = new SourceCollectingHostApplicationBuilder(new(StringComparer.Ordinal)
        {
            ["GoogleSecretManager:ProjectId"] = "mmca-prod",
            ["GoogleSecretManager:NamePrefix"] = "adc-prod-",
            ["GoogleSecretManager:ReloadIntervalMinutes"] = "15",
        });

        builder.AddGoogleSecretManagerConfiguration();

        var source = builder.Configuration.Sources.Should().ContainSingle()
            .Which.Should().BeOfType<GoogleSecretManagerConfigurationSource>().Subject;
        source.ProjectId.Should().Be("mmca-prod");
        source.NamePrefix.Should().Be("adc-prod-");
        source.ReloadInterval.Should().Be(TimeSpan.FromMinutes(15));
        builder.Services.Should().Contain(d =>
            d.ImplementationInstance is HostIntegrationMarker
            && ((HostIntegrationMarker)d.ImplementationInstance).Name == HostIntegrationNames.GoogleSecretManagerConfiguration);
    }

    [Fact]
    public void AddGoogleSecretManagerConfiguration_WithoutAReloadInterval_ReadsOnce()
    {
        var builder = new SourceCollectingHostApplicationBuilder(new(StringComparer.Ordinal)
        {
            ["GoogleSecretManager:ProjectId"] = "mmca-prod",
            ["GoogleSecretManager:NamePrefix"] = "adc-prod-",
        });

        builder.AddGoogleSecretManagerConfiguration();

        builder.Configuration.Sources.Should().ContainSingle()
            .Which.Should().BeOfType<GoogleSecretManagerConfigurationSource>()
            .Which.ReloadInterval.Should().BeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("fifteen")]
    [InlineData("1.5")]
    public void AddGoogleSecretManagerConfiguration_WithAnInvalidReloadInterval_Throws(string reloadInterval)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GoogleSecretManager:ProjectId"] = "mmca-prod",
            ["GoogleSecretManager:NamePrefix"] = "adc-prod-",
            ["GoogleSecretManager:ReloadIntervalMinutes"] = reloadInterval,
        });
        var sourceCountBefore = builder.Configuration.Sources.Count;

        var act = () => builder.AddGoogleSecretManagerConfiguration();

        act.Should().Throw<InvalidOperationException>(
            because: "a reload interval that cannot be honored must fail loudly rather than quietly fall back to never reloading")
            .WithMessage("*GoogleSecretManager:ReloadIntervalMinutes*");
        builder.Configuration.Sources.Count.Should().Be(sourceCountBefore);
        builder.Services.Should().NotContain(d => d.ImplementationInstance is HostIntegrationMarker);
    }

    /// <summary>
    /// A minimal <see cref="IHostApplicationBuilder"/> whose configuration collects sources instead of
    /// building them, so a Secret Manager source can be registered and asserted on without Google
    /// Cloud ever being contacted. Only the members the extension under test uses are functional.
    /// </summary>
    private sealed class SourceCollectingHostApplicationBuilder : IHostApplicationBuilder
    {
        public SourceCollectingHostApplicationBuilder(Dictionary<string, string?> settings) =>
            Configuration = new SourceCollectingConfigurationManager(settings);

        public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

        public IConfigurationManager Configuration { get; }

        public IHostEnvironment Environment { get; } = new StubHostEnvironment();

        public ILoggingBuilder Logging { get; } = new StubLoggingBuilder();

        public IMetricsBuilder Metrics { get; } = new StubMetricsBuilder();

        public IServiceCollection Services { get; } = new ServiceCollection();

        public void ConfigureContainer<TContainerBuilder>(
            IServiceProviderFactory<TContainerBuilder> factory,
            Action<TContainerBuilder>? configure = null)
            where TContainerBuilder : notnull =>
            throw new NotSupportedException("The source-collecting builder double does not build a container.");
    }

    /// <summary>
    /// An <see cref="IConfigurationManager"/> that reads its values from a fixed in-memory set and
    /// appends added sources to a plain list, deliberately never building or loading them.
    /// </summary>
    private sealed class SourceCollectingConfigurationManager : IConfigurationManager
    {
        private readonly IConfigurationRoot _values;

        public SourceCollectingConfigurationManager(Dictionary<string, string?> settings) =>
            _values = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        public IDictionary<string, object> Properties { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

        public IList<IConfigurationSource> Sources { get; } = [];

        public string? this[string key]
        {
            get => _values[key];
            set => _values[key] = value;
        }

        public IConfigurationBuilder Add(IConfigurationSource source)
        {
            Sources.Add(source);
            return this;
        }

        public IConfigurationRoot Build() => _values;

        public IEnumerable<IConfigurationSection> GetChildren() => _values.GetChildren();

        public IChangeToken GetReloadToken() => _values.GetReloadToken();

        public IConfigurationSection GetSection(string key) => _values.GetSection(key);
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "MMCA.Common.Gcp.Tests";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public string EnvironmentName { get; set; } = Environments.Development;
    }

    private sealed class StubLoggingBuilder : ILoggingBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
    }

    private sealed class StubMetricsBuilder : IMetricsBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
    }
}
