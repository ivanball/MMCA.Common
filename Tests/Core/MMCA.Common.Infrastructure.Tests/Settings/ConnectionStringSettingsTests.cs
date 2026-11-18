using AwesomeAssertions;
using MMCA.Common.Infrastructure.Persistence.DataSources;

namespace MMCA.Common.Infrastructure.Tests.Settings;

public class ConnectionStringSettingsTests
{
    [Fact]
    public void SectionName_IsConnectionStrings() =>
        ConnectionStringSettings.SectionName.Should().Be("ConnectionStrings");

    [Theory]
    [InlineData(nameof(ConnectionStringSettings.CosmosConnectionString))]
    [InlineData(nameof(ConnectionStringSettings.SqliteConnectionString))]
    [InlineData(nameof(ConnectionStringSettings.SQLServerConnectionString))]
    [InlineData(nameof(ConnectionStringSettings.SQLServerMigrationsAssembly))]
    public void Default_Property_IsEmpty(string property) =>
        typeof(ConnectionStringSettings).GetProperty(property)!.GetValue(new ConnectionStringSettings())
            .Should().Be(string.Empty);

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new ConnectionStringSettings
        {
            CosmosConnectionString = "AccountEndpoint=https://cosmos.example.com;AccountKey=key",
            SqliteConnectionString = "Data Source=app.db",
            SQLServerConnectionString = "Server=.;Database=AppDb;Trusted_Connection=True",
            SQLServerMigrationsAssembly = "MyApp.Migrations",
        };

        sut.CosmosConnectionString.Should().Be("AccountEndpoint=https://cosmos.example.com;AccountKey=key");
        sut.SqliteConnectionString.Should().Be("Data Source=app.db");
        sut.SQLServerConnectionString.Should().Be("Server=.;Database=AppDb;Trusted_Connection=True");
        sut.SQLServerMigrationsAssembly.Should().Be("MyApp.Migrations");
    }
}
