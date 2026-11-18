using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace MMCA.Common.Tests.Shared.Hosting;

/// <summary>
/// A minimal <see cref="IHostApplicationBuilder"/> whose configuration collects sources instead of
/// building them, so a secret-store configuration source (Key Vault, Secrets Manager, Secret Manager)
/// can be registered and asserted on without the vendor ever being contacted. Only the members the
/// extensions under test use are functional.
/// <para>
/// Linked as source into each cloud adapter test project (see the <c>Compile Include</c> in its
/// project file), so every project compiles its own internal copy.
/// </para>
/// </summary>
internal sealed class SourceCollectingHostApplicationBuilder : IHostApplicationBuilder
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SourceCollectingHostApplicationBuilder"/> class.
    /// </summary>
    /// <param name="settings">The configuration values the extension under test reads.</param>
    /// <param name="applicationName">
    /// The host environment's application name; <see langword="null"/> uses the name of the test
    /// assembly this file is compiled into.
    /// </param>
    public SourceCollectingHostApplicationBuilder(Dictionary<string, string?> settings, string? applicationName = null)
    {
        Configuration = new SourceCollectingConfigurationManager(settings);
        Environment = new StubHostEnvironment
        {
            ApplicationName = applicationName ?? typeof(SourceCollectingHostApplicationBuilder).Assembly.GetName().Name!,
        };
    }

    public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

    public IConfigurationManager Configuration { get; }

    public IHostEnvironment Environment { get; }

    public ILoggingBuilder Logging { get; } = new StubLoggingBuilder();

    public IMetricsBuilder Metrics { get; } = new StubMetricsBuilder();

    public IServiceCollection Services { get; } = new ServiceCollection();

    public void ConfigureContainer<TContainerBuilder>(
        IServiceProviderFactory<TContainerBuilder> factory,
        Action<TContainerBuilder>? configure = null)
        where TContainerBuilder : notnull =>
        throw new NotSupportedException("The source-collecting builder double does not build a container.");

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
        public string ApplicationName { get; set; } = string.Empty;

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
