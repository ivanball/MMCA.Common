using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.Modules;
using MMCA.Common.Application.Settings;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// Unit tests for <c>AddModuleHost</c>, the settings-bind plus <see cref="ModuleLoader"/>
/// construction every module-hosting service repeated verbatim. The contract is deliberately narrow:
/// it binds, builds and registers, and it does NOT run discovery, because discovery has to sit at a
/// host-chosen position inside the ADR-014 application pipeline.
/// </summary>
public sealed class ModuleHostExtensionsTests
{
    private static WebApplicationBuilder CreateBuilder(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(
            settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));

        return builder;
    }

    private static (string Key, string? Value)[] MinimalApplicationSettings() =>
        [("ApplicationSettings:MaxPageSize", "250")];

    [Fact]
    public void BindsApplicationSettingsForBothTheOptionsGraphAndTheReturnedContext()
    {
        var builder = CreateBuilder(("ApplicationSettings:MaxPageSize", "250"));

        var moduleHost = builder.AddModuleHost();

        moduleHost.ApplicationSettings.MaxPageSize.Should().Be(250);

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<ApplicationSettings>>().Value.MaxPageSize.Should().Be(250);
    }

    [Fact]
    public void MissingApplicationSettingsSection_FailsFast()
    {
        var builder = CreateBuilder();

        var act = () => builder.AddModuleHost();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("ApplicationSettings section is not configured.");
    }

    [Fact]
    public void BindsModulesSettings()
    {
        var builder = CreateBuilder(
            [.. MinimalApplicationSettings(), ("Modules:Tickets:Enabled", "true")]);

        var moduleHost = builder.AddModuleHost();

        moduleHost.ModulesSettings.Should().ContainKey("Tickets");
        moduleHost.ModulesSettings["Tickets"].Enabled.Should().BeTrue();
    }

    [Fact]
    public void MissingModulesSection_YieldsEmptySettingsRatherThanNull()
    {
        var builder = CreateBuilder(MinimalApplicationSettings());

        var moduleHost = builder.AddModuleHost();

        moduleHost.ModulesSettings.Should().BeEmpty(
            "a host with no Modules section runs every discovered module, exactly as before the hoist");
    }

    [Fact]
    public void RegistersTheLoaderAsASingletonSoTheRestOfTheHostResolvesTheSameInstance()
    {
        var builder = CreateBuilder(MinimalApplicationSettings());

        var moduleHost = builder.AddModuleHost();

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<ModuleLoader>().Should().BeSameAs(moduleHost.ModuleLoader);
    }

    [Fact]
    public void SuppliedLogger_IsUsedForDiscoveryDiagnostics()
    {
        var builder = CreateBuilder(MinimalApplicationSettings());
        ILogger<ModuleLoader> logger = NullLoggerFactory.Instance.CreateLogger<ModuleLoader>();

        var moduleHost = builder.AddModuleHost(logger);

        moduleHost.ModuleLoader.Logger.Should().BeSameAs(logger);
    }

    [Fact]
    public void NoLogger_LeavesTheLoadersOwnNullLoggerDefault()
    {
        var builder = CreateBuilder(MinimalApplicationSettings());

        var moduleHost = builder.AddModuleHost();

        moduleHost.ModuleLoader.Logger.Should().BeOfType<NullLogger<ModuleLoader>>();
    }

    [Fact]
    public void DoesNotRunDiscoveryItself()
    {
        var builder = CreateBuilder(MinimalApplicationSettings());

        var moduleHost = builder.AddModuleHost();

        ReadCapturedModulesSettings(moduleHost.ModuleLoader).Should().BeNull(
            "discovery belongs inside the host's application pipeline, between AddApplication and AddApplicationDecorators");
    }

    // There is deliberately no test driving RegisterModules through to a real discovery pass: it
    // calls ModuleLoader's AppDomain-scanning overload, exactly as the hosts do, and in a test
    // process that scan sweeps in whatever IModule types other test classes happen to have created
    // (Moq's dynamic proxies among them), which would make the outcome depend on run order.
    [Fact]
    public void RegisterModules_RejectsANullServiceCollection()
    {
        var builder = CreateBuilder(MinimalApplicationSettings());
        var moduleHost = builder.AddModuleHost();

        var act = () => moduleHost.RegisterModules(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Reads the settings the loader captured on its last <c>DiscoverAndRegister</c> call. Private
    /// state, but it is the only observable proof that the hoisted bind actually reached discovery
    /// (the loader is sealed, so it cannot be mocked); the same reflection idiom is already used by
    /// <c>DependencyInjectionTests</c>.
    /// </summary>
    private static ModulesSettings? ReadCapturedModulesSettings(ModuleLoader loader) =>
        (ModulesSettings?)typeof(ModuleLoader)
            .GetField("_modulesSettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(loader);
}
