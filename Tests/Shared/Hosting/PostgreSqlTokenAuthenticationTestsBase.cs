using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using Npgsql;

namespace MMCA.Common.Tests.Shared.Hosting;

/// <summary>
/// The PostgreSQL token-authentication contract the RDS IAM, Cloud SQL IAM and Microsoft Entra adapters
/// share: off unless the gate key is true; when on, one data-source configurator of the adapter's type
/// that the keyed data source is built through, however often the call is made; every encrypted SSL
/// mode accepted, a weak one refused again when a data source is built, and the other engines left
/// alone. Each subclass supplies the registration call, its gate key, the host and user its connection
/// strings carry, and a configurator built without a credential; the token source and anything only one
/// vendor registers stay in the subclass.
/// </summary>
/// <remarks>Linked as source into MMCA.Common.Aws.Tests, MMCA.Common.Gcp.Tests and MMCA.Common.Azure.Tests.</remarks>
public abstract class PostgreSqlTokenAuthenticationTestsBase
{
    /// <summary>The configuration key that switches the adapter on.</summary>
    protected abstract string EnabledKey { get; }

    /// <summary>The server host every connection string in the shared cases names.</summary>
    protected abstract string ServerHost { get; }

    /// <summary>The database user every connection string in the shared cases names.</summary>
    protected abstract string UserName { get; }

    /// <summary>A connection string with <c>SSL Mode=Require</c>, which every adapter accepts.</summary>
    protected string EncryptedConnectionString =>
        $"Host={ServerHost};Port=5432;Database=orders;Username={UserName};SSL Mode=Require";

    /// <summary>Calls the adapter's registration extension.</summary>
    protected abstract void Register(IServiceCollection services, IConfiguration configuration);

    /// <summary>A configurator of the type the adapter registers, built without contacting the vendor.</summary>
    protected abstract INpgsqlDataSourceConfigurator CreateConfigurator();

    /// <summary>The settings plus the gate key set to true (and whatever else the adapter needs to register).</summary>
    protected virtual IConfiguration EnabledConfiguration(Dictionary<string, string?> settings)
    {
        settings[EnabledKey] = "true";
        return ConfigurationWith(settings);
    }

    /// <summary>What a call with the gate off must have left in the collection.</summary>
    protected virtual void AssertNothingRegistered(IServiceCollection services)
    {
        services.Should().NotContain(d => d.ServiceType == typeof(INpgsqlDataSourceConfigurator));
        services.Should().NotContain(d => d.ServiceType == typeof(NpgsqlDataSource));
    }

    /// <summary>Extra registrations an enabled call must make (each adapter's startup receipt).</summary>
    protected virtual void AssertEnabledRegistrations(IServiceCollection services)
    {
    }

    protected static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void WhenNotEnabled_RegistersNothing(string? enabled)
    {
        var services = new ServiceCollection();

        // No SSL mode on purpose: with the gate off nothing is validated either.
        Register(services, ConfigurationWith(new()
        {
            [EnabledKey] = enabled,
            ["ConnectionStrings:PostgreSQLConnectionString"] = "Host=localhost;Database=app;Username=app;Password=secret",
        }));

        AssertNothingRegistered(services);
    }

    [Fact]
    public void WhenEnabled_RegistersTheConfiguratorAndTheDataSource()
    {
        var services = new ServiceCollection();

        Register(services, EnabledConfiguration(new()
        {
            ["ConnectionStrings:PostgreSQLConnectionString"] = EncryptedConnectionString,
        }));

        AssertEnabledRegistrations(services);

        using var provider = services.BuildServiceProvider();
        provider.GetServices<INpgsqlDataSourceConfigurator>().Should().ContainSingle()
            .Which.Should().BeOfType(CreateConfigurator().GetType());

        // Building it is what applies the configurator: a periodic password provider plus a static
        // password would make Npgsql refuse the build. No token is requested at registration or build.
        provider.GetKeyedService<NpgsqlDataSource>(EncryptedConnectionString).Should().NotBeNull();
    }

    [Fact]
    public void CalledTwice_RegistersOneConfigurator()
    {
        var services = new ServiceCollection();
        var configuration = EnabledConfiguration(new() { ["ConnectionStrings:PostgreSQLConnectionString"] = EncryptedConnectionString });

        Register(services, configuration);
        Register(services, configuration);

        services.Count(d => d.ServiceType == typeof(INpgsqlDataSourceConfigurator)).Should().Be(1);
    }

    [Theory]
    [InlineData("Require")]
    [InlineData("VerifyCA")]
    [InlineData("VerifyFull")]
    public void AnEncryptedConnectionString_IsAccepted(string sslMode)
    {
        var services = new ServiceCollection();

        var act = () => Register(services, EnabledConfiguration(new()
        {
            ["ConnectionStrings:PostgreSQLConnectionString"] = $"Host={ServerHost};Database=orders;Username={UserName};SSL Mode={sslMode}",
            ["DataSources:Orders:PostgreSQLConnectionString"] = $"Host={ServerHost};Database=orders;Username={UserName};SSL Mode={sslMode}",
        }));

        act.Should().NotThrow();
    }

    [Fact]
    public void AWeakSslMode_AlsoFailsTheDataSourceBuild_ForAConnectionStringRegistrationNeverSaw()
    {
        var configurator = CreateConfigurator();

        var act = () => configurator.Configure(new NpgsqlDataSourceBuilder($"Host={ServerHost};Database=orders;Username={UserName}"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*SSL Mode=Require*");
    }

    [Fact]
    public void OtherEngines_AreLeftAlone()
    {
        var services = new ServiceCollection();

        var act = () => Register(services, EnabledConfiguration(new()
        {
            ["ConnectionStrings:SQLServerConnectionString"] = "Server=10.20.0.4;Database=app;Encrypt=false",
            ["ConnectionStrings:SqliteConnectionString"] = "Data Source=app.db",
            ["DataSources:Catalog:CosmosConnectionString"] = "AccountEndpoint=https://cosmos.example:443/;AccountKey=a2V5",
        }));

        act.Should().NotThrow("token authentication applies to PostgreSQL connection strings only");
    }
}
