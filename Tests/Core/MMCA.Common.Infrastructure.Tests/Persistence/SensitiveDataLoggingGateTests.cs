using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Infrastructure.Persistence;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

/// <summary>
/// Pins the AND gate in front of EF's <c>EnableSensitiveDataLogging</c>. The flag renders parameter
/// VALUES into logs and exception messages: email addresses, tokens, password hashes, payment
/// identifiers. Configuration travels (a copied appsettings file, a promoted image, an environment
/// variable set once and forgotten), so asking for it must not be enough on its own. The host has to
/// ALSO report the Development environment, and an unknown environment fails closed.
/// </summary>
public sealed class SensitiveDataLoggingGateTests
{
    // "development" is included because IsDevelopment() compares case-INSENSITIVELY: a host that
    // spells its environment in lower case is still Development, and the gate must not accidentally
    // read as production-safe just because of casing.
    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    public void IsEnabled_WithTheFlagOnInDevelopment_IsTrue(string environmentName) =>
        SensitiveDataLoggingGate
            .IsEnabled(new PersistenceSettings { EnableSensitiveDataLogging = true }, Environment(environmentName))
            .Should().BeTrue();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Local")]
    [InlineData("")]
    public void IsEnabled_WithTheFlagOnOutsideDevelopment_IsFalse(string environmentName) =>
        SensitiveDataLoggingGate
            .IsEnabled(new PersistenceSettings { EnableSensitiveDataLogging = true }, Environment(environmentName))
            .Should().BeFalse("a leaked flag must never put customer data into a deployed log sink");

    [Fact]
    public void IsEnabled_WithTheFlagOffInDevelopment_IsFalse() =>
        SensitiveDataLoggingGate
            .IsEnabled(new PersistenceSettings(), Environment("Development"))
            .Should().BeFalse("the default is off, which is the behavior before the flag existed");

    // Design-time tooling and a directly-constructed test context register no environment.
    [Fact]
    public void IsEnabled_WithNoEnvironmentRegistered_IsFalse() =>
        SensitiveDataLoggingGate
            .IsEnabled(new PersistenceSettings { EnableSensitiveDataLogging = true }, environment: null)
            .Should().BeFalse("an unknown environment is not a Development environment");

    [Fact]
    public void IsEnabled_WithNoSettingsBound_IsFalse() =>
        SensitiveDataLoggingGate.IsEnabled(settings: null, Environment("Development")).Should().BeFalse();

    [Fact]
    public void EnableSensitiveDataLogging_DefaultsToOff() =>
        new PersistenceSettings().EnableSensitiveDataLogging.Should().BeFalse();

    private static StubHostEnvironment Environment(string environmentName) =>
        new() { EnvironmentName = environmentName };

    /// <summary>Minimal <see cref="IHostEnvironment"/>: only the environment name is read.</summary>
    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";

        public string ApplicationName { get; set; } = "Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
