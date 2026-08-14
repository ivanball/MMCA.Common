using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace MMCA.Common.Aspire.Tests.Configuration;

/// <summary>
/// Guards the configuration gate on <c>AddCommonKeyVaultConfiguration</c>: no <c>KeyVault:Uri</c>
/// means the host gains no configuration source and takes no Azure dependency at startup, while a
/// configured URI appends the Key Vault source that later reads the vault.
/// <para>
/// No test here touches the network, and the two builder shapes are what keeps that true. The tests
/// that must NOT add a source (the gate, and the two malformed-configuration cases, which throw
/// before the source is built) run against a real <see cref="HostApplicationBuilder"/>, because that
/// is the type a host actually uses. The tests that DO add a source run against a builder double
/// whose configuration merely collects sources: a real <c>ConfigurationManager</c> builds and loads
/// every source the instant it is added, and loading the Key Vault source means a live call to the
/// vault. The double therefore proves the registration without the call, which is exactly the part
/// this extension is responsible for.
/// </para>
/// </summary>
public sealed class KeyVaultConfigurationExtensionsTests
{
    private const string KeyVaultAssemblyName = "Azure.Extensions.AspNetCore.Configuration.Secrets";

    [Fact]
    public void AddCommonKeyVaultConfiguration_WithoutVaultUri_AddsNoConfigurationSource()
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = builder.Configuration;
        var sourceCountBefore = configuration.Sources.Count;

        builder.AddCommonKeyVaultConfiguration();

        configuration.Sources.Count
            .Should().Be(
                sourceCountBefore,
                because: "an unconfigured host must gain no configuration source at all, so local development and tests never reach for a vault at startup");

        configuration.Sources.Any(IsKeyVaultSource)
            .Should().BeFalse(
                because: "the gate key is what decides whether the host takes an Azure dependency, and it is absent here");
    }

    [Fact]
    public void AddCommonKeyVaultConfiguration_WithVaultUri_AppendsTheKeyVaultConfigurationSource()
    {
        var builder = SourceCollectingBuilderWith(new(StringComparer.Ordinal)
        {
            ["KeyVault:Uri"] = "https://mmca-tests.vault.azure.net/",
        });

        builder.AddCommonKeyVaultConfiguration();

        builder.Configuration.Sources
            .Should().ContainSingle(
                because: "a configured vault URI must add exactly one configuration source");

        IsKeyVaultSource(builder.Configuration.Sources[^1])
            .Should().BeTrue(
                because: "the source has to be the Key Vault one specifically, and it has to be appended last so vault secrets override the file and environment values added before it");
    }

    [Fact]
    public void AddCommonKeyVaultConfiguration_WithReloadIntervalMinutes_StillAppendsTheKeyVaultConfigurationSource()
    {
        var builder = SourceCollectingBuilderWith(new(StringComparer.Ordinal)
        {
            ["KeyVault:Uri"] = "https://mmca-tests.vault.azure.net/",
            ["KeyVault:ReloadIntervalMinutes"] = "15",
        });

        builder.AddCommonKeyVaultConfiguration();

        IsKeyVaultSource(builder.Configuration.Sources[^1])
            .Should().BeTrue(
                because: "a valid reload interval must be accepted and still produce the vault source; the interval itself is carried on options held by a source type the Azure package keeps internal, so it is not observable from here without reading private state");
    }

    [Fact]
    public void AddCommonKeyVaultConfiguration_WithMalformedVaultUri_Throws()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["KeyVault:Uri"] = "not-a-vault-uri",
        });

        var act = () => builder.AddCommonKeyVaultConfiguration();

        act.Should().Throw<UriFormatException>(
            because: "a typo in the vault URI must stop the host at startup rather than let it run on whatever configuration it happened to have");

        builder.Configuration.Sources.Any(IsKeyVaultSource)
            .Should().BeFalse(
                because: "the URI is validated before the source is constructed, so nothing is added and no vault call is attempted");
    }

    [Fact]
    public void AddCommonKeyVaultConfiguration_WithNonPositiveReloadIntervalMinutes_Throws()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["KeyVault:Uri"] = "https://mmca-tests.vault.azure.net/",
            ["KeyVault:ReloadIntervalMinutes"] = "0",
        });

        var act = () => builder.AddCommonKeyVaultConfiguration();

        act.Should().Throw<InvalidOperationException>(
            because: "a reload interval that cannot be honored must fail loudly: quietly falling back to no reload would leave a rotated secret unused until the next restart, and nobody would notice until it mattered")
            .WithMessage("*KeyVault:ReloadIntervalMinutes*");
    }

    private static bool IsKeyVaultSource(IConfigurationSource source) =>
        string.Equals(source.GetType().Assembly.GetName().Name, KeyVaultAssemblyName, StringComparison.Ordinal);

    private static SourceCollectingHostApplicationBuilder SourceCollectingBuilderWith(Dictionary<string, string?> settings) =>
        new(settings);

    /// <summary>
    /// A minimal <see cref="IHostApplicationBuilder"/> whose configuration collects sources instead of
    /// building them, so a Key Vault source can be registered and asserted on without the vault ever
    /// being contacted. Only the members the extension under test uses are functional.
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
        public string ApplicationName { get; set; } = "MMCA.Common.Aspire.Tests";

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
