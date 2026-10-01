using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// <c>InitializeDatabaseUnlessDesignTimeAsync</c>, the guard every ADC and Store service host wrapped
/// around <c>InitializeDatabaseAsync</c> by hand. Build-time OpenAPI generation
/// (<c>MmcaGenerateOpenApiDocument</c>) starts the host with no database reachable and sets
/// <c>MmcaOpenApiDesignTime</c>; the schema step is the one startup action that throws rather than
/// logs when it cannot connect, so that run alone skips it. The switch is honoured only in
/// Development (ADR-122: dev-only relaxations fail closed), so no other environment can be talked
/// out of initializing its schema.
/// <para>
/// "Initialization ran" is observed through the strategy check, which is the first thing
/// <c>InitializeDatabaseAsync</c> does: an unknown strategy throws before any database is opened.
/// </para>
/// </summary>
public sealed class DesignTimeDatabaseInitializationTests
{
    private const string DesignTimeKey = "MmcaOpenApiDesignTime";

    [Fact]
    public async Task DevelopmentDesignTimeRun_SkipsInitialization()
    {
        var built = Build(Environments.Development, designTime: "true");
        await using WebApplication app = built.App;
        var moduleHost = built.ModuleHost;

        var act = () => app.InitializeDatabaseUnlessDesignTimeAsync(moduleHost, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("the design-time run has no database to initialize");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task DevelopmentWithoutTheSwitch_Initializes(string? designTime)
    {
        var built = Build(Environments.Development, designTime);
        await using WebApplication app = built.App;
        var moduleHost = built.ModuleHost;

        var act = () => app.InitializeDatabaseUnlessDesignTimeAsync(moduleHost, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Valid values are: Migrate, None*");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task OutsideDevelopment_TheSwitchIsIgnoredAndInitializationRuns(string environment)
    {
        var built = Build(environment, designTime: "true");
        await using WebApplication app = built.App;
        var moduleHost = built.ModuleHost;

        var act = () => app.InitializeDatabaseUnlessDesignTimeAsync(moduleHost, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a deployed host can never be talked out of validating its schema");
    }

    [Fact]
    public async Task NullModuleHost_Throws()
    {
        var built = Build(Environments.Development, designTime: "true");
        await using WebApplication app = built.App;

        var act = () => app.InitializeDatabaseUnlessDesignTimeAsync(null!, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    private static (WebApplication App, ModuleHostContext ModuleHost) Build(string environment, string? designTime)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
        });

        builder.Logging.ClearProviders();

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Not a valid strategy, so a run that reaches InitializeDatabaseAsync throws at once.
            ["ApplicationSettings:DatabaseInitStrategy"] = "NotAStrategy",
        };

        if (designTime is not null)
        {
            settings[DesignTimeKey] = designTime;
        }

        builder.Configuration.AddInMemoryCollection(settings);

        var moduleHost = builder.AddModuleHost([]);
        return (builder.Build(), moduleHost);
    }
}
