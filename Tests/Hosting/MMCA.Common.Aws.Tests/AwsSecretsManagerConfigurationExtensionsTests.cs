using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Aws.Secrets;

namespace MMCA.Common.Aws.Tests;

/// <summary>
/// Guards the configuration gate on <c>AddAwsSecretsManagerConfiguration</c>: no
/// <c>SecretsManager:NamePrefix</c> means the host gains no configuration source, no AWS dependency
/// and no tripwire receipt, while a configured prefix appends the Secrets Manager source.
/// <para>
/// No test here touches the network, for the same reason as the Key Vault sibling's tests: the cases
/// that must NOT add a source run against a real <see cref="HostApplicationBuilder"/>, and the case
/// that DOES add one runs against a builder double whose configuration collects sources without
/// loading them, because a real <c>ConfigurationManager</c> loads each source the instant it is added.
/// </para>
/// </summary>
public sealed class AwsSecretsManagerConfigurationExtensionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAwsSecretsManagerConfiguration_WithoutANamePrefix_IsANoOp(string? prefix)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SecretsManager:NamePrefix"] = prefix,
            ["SecretsManager:Region"] = "us-east-1",
        });
        var sourceCountBefore = builder.Configuration.Sources.Count;

        builder.AddAwsSecretsManagerConfiguration();

        builder.Configuration.Sources.Count.Should().Be(
            sourceCountBefore,
            because: "an unconfigured host must gain no configuration source, so local development and tests never reach for AWS at startup");
        builder.Services.Should().NotContain(
            d => d.ImplementationInstance is HostIntegrationMarker,
            because: "a call that added nothing leaves no receipt, so a prefix that appears later still trips the startup check");
    }

    [Fact]
    public void AddAwsSecretsManagerConfiguration_WithANamePrefix_AppendsTheSourceAndTheReceipt()
    {
        var builder = new SourceCollectingHostApplicationBuilder(new(StringComparer.Ordinal)
        {
            ["SecretsManager:NamePrefix"] = "adc/prod/",
            ["SecretsManager:Region"] = "eu-west-1",
            ["SecretsManager:ReloadIntervalMinutes"] = "15",
        });

        builder.AddAwsSecretsManagerConfiguration();

        var source = builder.Configuration.Sources.Should().ContainSingle()
            .Which.Should().BeOfType<AwsSecretsManagerConfigurationSource>().Subject;
        source.NamePrefix.Should().Be("adc/prod/");
        source.ReloadInterval.Should().Be(TimeSpan.FromMinutes(15));
        builder.Services.Should().Contain(d =>
            d.ImplementationInstance is HostIntegrationMarker
            && ((HostIntegrationMarker)d.ImplementationInstance).Name == HostIntegrationNames.AwsSecretsManagerConfiguration);
    }

    [Fact]
    public void AddAwsSecretsManagerConfiguration_WithoutAReloadInterval_ReadsOnce()
    {
        var builder = new SourceCollectingHostApplicationBuilder(new(StringComparer.Ordinal)
        {
            ["SecretsManager:NamePrefix"] = "adc/prod/",
        });

        builder.AddAwsSecretsManagerConfiguration();

        builder.Configuration.Sources.Should().ContainSingle()
            .Which.Should().BeOfType<AwsSecretsManagerConfigurationSource>()
            .Which.ReloadInterval.Should().BeNull();
    }

    [Theory]
    [InlineData("AKIDEXAMPLE", null)]
    [InlineData(null, "secret")]
    public void AddAwsSecretsManagerConfiguration_WithHalfAKeyPair_FailsNamingTheSection(string? accessKeyId, string? secret)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SecretsManager:NamePrefix"] = "adc/prod/",
            ["SecretsManager:Region"] = "us-east-1",
            ["SecretsManager:AccessKeyId"] = accessKeyId,
            ["SecretsManager:SecretAccessKey"] = secret,
        });
        var sourceCountBefore = builder.Configuration.Sources.Count;

        var act = () => builder.AddAwsSecretsManagerConfiguration();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SecretsManager:AccessKeyId and SecretsManager:SecretAccessKey must be set together*");
        builder.Configuration.Sources.Count.Should().Be(sourceCountBefore);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("fifteen")]
    [InlineData("1.5")]
    public void AddAwsSecretsManagerConfiguration_WithAnInvalidReloadInterval_Throws(string reloadInterval)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SecretsManager:NamePrefix"] = "adc/prod/",
            ["SecretsManager:Region"] = "us-east-1",
            ["SecretsManager:ReloadIntervalMinutes"] = reloadInterval,
        });
        var sourceCountBefore = builder.Configuration.Sources.Count;

        var act = () => builder.AddAwsSecretsManagerConfiguration();

        act.Should().Throw<InvalidOperationException>(
            because: "a reload interval that cannot be honored must fail loudly rather than quietly fall back to never reloading")
            .WithMessage("*SecretsManager:ReloadIntervalMinutes*");
        builder.Configuration.Sources.Count.Should().Be(sourceCountBefore);
    }

    /// <summary>
    /// A minimal <see cref="IHostApplicationBuilder"/> whose configuration collects sources instead of
    /// building them, so a Secrets Manager source can be registered and asserted on without AWS ever
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
        public string ApplicationName { get; set; } = "MMCA.Common.Aws.Tests";

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
