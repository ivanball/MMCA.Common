using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Integrations;

namespace MMCA.Common.Tests.Shared.Hosting;

/// <summary>
/// The secret-store configuration contract the Secrets Manager and Secret Manager extensions share,
/// once their gate is complete: the call appends exactly one source of the vendor's type carrying the
/// gate and the reload interval, plus the tripwire receipt; no reload interval means the store is read
/// once; and an interval that cannot be honored throws naming the key, adding neither a source nor a
/// receipt. The source-adding cases run against <see cref="SourceCollectingHostApplicationBuilder"/>,
/// because a real <c>ConfigurationManager</c> loads each source the instant it is added. Each subclass
/// supplies the call, its configuration section, the settings that complete its gate, its source type
/// and receipt name; the incomplete-gate cases (whose data differs per vendor) stay in the subclass.
/// </summary>
/// <remarks>Linked as source into MMCA.Common.Aws.Tests and MMCA.Common.Gcp.Tests.</remarks>
public abstract class SecretStoreConfigurationExtensionsTestsBase
{
    /// <summary>The configuration section the extension reads, such as <c>SecretsManager</c>.</summary>
    protected abstract string Section { get; }

    /// <summary>The configuration source type the extension appends.</summary>
    protected abstract Type SourceType { get; }

    /// <summary>The <see cref="HostIntegrationMarker"/> name the extension leaves as its receipt.</summary>
    protected abstract string MarkerName { get; }

    /// <summary>A fresh, ordinal settings set that completes the gate and sets no reload interval.</summary>
    protected abstract Dictionary<string, string?> CompleteGate();

    /// <summary>Calls the extension under test.</summary>
    protected abstract void AddConfiguration(IHostApplicationBuilder builder);

    /// <summary>The reload interval the appended source carries.</summary>
    protected abstract TimeSpan? ReloadIntervalOf(IConfigurationSource source);

    /// <summary>Asserts the appended source carries the gate values <see cref="CompleteGate"/> set.</summary>
    protected abstract void AssertGateCarried(IConfigurationSource source);

    [Fact]
    public void WithACompleteGate_AppendsTheSourceAndTheReceipt()
    {
        var settings = CompleteGate();
        settings[$"{Section}:ReloadIntervalMinutes"] = "15";
        var builder = new SourceCollectingHostApplicationBuilder(settings);

        AddConfiguration(builder);

        var source = builder.Configuration.Sources.Should().ContainSingle().Subject;
        source.Should().BeOfType(SourceType);
        AssertGateCarried(source);
        ReloadIntervalOf(source).Should().Be(TimeSpan.FromMinutes(15));
        builder.Services.Should().Contain(d =>
            d.ImplementationInstance is HostIntegrationMarker
            && ((HostIntegrationMarker)d.ImplementationInstance).Name == MarkerName);
    }

    [Fact]
    public void WithoutAReloadInterval_ReadsOnce()
    {
        var builder = new SourceCollectingHostApplicationBuilder(CompleteGate());

        AddConfiguration(builder);

        var source = builder.Configuration.Sources.Should().ContainSingle().Subject;
        source.Should().BeOfType(SourceType);
        ReloadIntervalOf(source).Should().BeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("fifteen")]
    [InlineData("1.5")]
    public void WithAnInvalidReloadInterval_Throws(string reloadInterval)
    {
        var builder = Host.CreateApplicationBuilder();
        var settings = CompleteGate();
        settings[$"{Section}:ReloadIntervalMinutes"] = reloadInterval;
        builder.Configuration.AddInMemoryCollection(settings);
        var sourceCountBefore = builder.Configuration.Sources.Count;

        var act = () => AddConfiguration(builder);

        act.Should().Throw<InvalidOperationException>(
            because: "a reload interval that cannot be honored must fail loudly rather than quietly fall back to never reloading")
            .WithMessage($"*{Section}:ReloadIntervalMinutes*");
        builder.Configuration.Sources.Count.Should().Be(sourceCountBefore);
        builder.Services.Should().NotContain(d => d.ImplementationInstance is HostIntegrationMarker);
    }
}
