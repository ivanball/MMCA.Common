using Aspire.Hosting.Testing;
using MMCA.Common.Testing.Aspire.Fixtures;
using MMCA.Common.Testing.Aspire.Preconditions;

namespace MMCA.Common.Testing.Aspire.AppHostTests;

/// <summary>
/// The first subclass of <see cref="AppHostFixtureBase{TAppHost}"/>, and the shape a consumer's own
/// smoke fixture takes: a type parameter naming the AppHost, and overrides for the things that are
/// genuinely per-stack. Everything else (the precondition gate, the ephemeral keypair, the
/// per-resource readiness wait, the environment restore) is inherited.
/// </summary>
public sealed class SampleAppHostFixture
    : AppHostFixtureBase<Projects.MMCA_Common_Testing_Aspire_AppHostTests_SampleAppHost>
{
    /// <summary>The sample service's resource name in the AppHost, on the HTTP/1.1 cleartext profile.</summary>
    public const string ServiceResourceName = "sample";

    /// <summary>
    /// The same service on the Http2-only cleartext profile, which is the only one that serves h2c:
    /// with no TLS there is no ALPN, so an Http1AndHttp2 endpoint answers HTTP/1.1 and nothing else.
    /// </summary>
    public const string H2cServiceResourceName = "sample-h2c";

    /// <summary>The logical data source name the AppHost routes the SQLite file to.</summary>
    public const string LogicalDataSourceName = "Sample";

    /// <summary>
    /// A key pushed through <see cref="ConfigureBuilder"/> so a test can prove the hook runs against
    /// the real builder before the application is built.
    /// </summary>
    public const string MarkerConfigurationKey = "MMCA:AppHostTests:Marker";

    /// <summary>The value <see cref="MarkerConfigurationKey"/> carries.</summary>
    public const string MarkerConfigurationValue = "configured-by-the-fixture";

    /// <summary>
    /// Opt-in only, with no container requirement: this AppHost declares one project resource and a
    /// SQLite file, so it needs no container runtime. A consumer's real stack (SQL Server, Redis, a
    /// broker) keeps the inherited default, which also demands one.
    /// </summary>
    protected override AppHostEnvironmentRequirement RequiredEnvironment =>
        AppHostEnvironmentRequirement.OptIn;

    /// <summary>
    /// Tighter than the inherited five minutes each, because nothing here pulls a container image.
    /// Still generous enough that a cold NuGet restore on a shared runner cannot red the tier.
    /// </summary>
    protected override AppHostReadinessBudget Budget { get; } =
        new(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3));

    /// <inheritdoc />
    protected override void ConfigureBuilder(IDistributedApplicationTestingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Where a consumer pushes test-only configuration: a shortened Outbox:PollingIntervalSeconds
        // so a test does not wait a production poll interval, an E2E lift switch, a replaced service
        // registration. Here it is one marker key, which is the smallest thing that proves the hook
        // reaches the real builder before it is built.
        builder.Configuration[MarkerConfigurationKey] = MarkerConfigurationValue;
    }
}
