using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using MMCA.Common.Testing.Fixtures;
using Xunit;

namespace MMCA.Common.Testing.Tests.Fixtures;

/// <summary>
/// Covers the container-free logic of <see cref="CrossServiceFixtureBase"/>: connection-string composition
/// and the first-write-wins environment snapshot. The container lifecycle itself needs a Docker daemon and
/// is exercised by the consumers' nightly cross-service tiers; everything asserted here would otherwise only
/// be verified there.
/// </summary>
public class CrossServiceFixtureBaseTests
{
    private const string BaseConnectionString = "Server=127.0.0.1,1433;User Id=sa;Password=Test1234!;Database=master";

    [Fact]
    public void ComposeConnectionString_PointsAtTheRequestedCatalog()
    {
        var composed = CrossServiceFixtureBase.ComposeConnectionString(BaseConnectionString, "ADC_Conference");

        var builder = new SqlConnectionStringBuilder(composed);
        builder.InitialCatalog.Should().Be("ADC_Conference");
        builder.TrustServerCertificate.Should().BeTrue();
        builder.DataSource.Should().Be("127.0.0.1,1433");
    }

    [Fact]
    public void ComposeConnectionString_WithoutAnApplicationName_StaysOrdinallyEqualToTheTopLevelString()
    {
        var first = CrossServiceFixtureBase.ComposeConnectionString(BaseConnectionString, "ADC_Conference");
        var second = CrossServiceFixtureBase.ComposeConnectionString(BaseConnectionString, "ADC_Conference");

        first.Should().Be(second);
        new SqlConnectionStringBuilder(first).ApplicationName
            .Should().Be(new SqlConnectionStringBuilder().ApplicationName);
    }

    [Fact]
    public void ComposeConnectionString_WithAnApplicationName_DiffersOrdinallyFromThePlainOne()
    {
        // The whole named-data-source trick depends on this difference: an ordinally identical connection
        // string collapses the named source onto Default and the hosts then share one EF model cache entry.
        var plain = CrossServiceFixtureBase.ComposeConnectionString(BaseConnectionString, "ADC_Conference");
        var named = CrossServiceFixtureBase.ComposeConnectionString(BaseConnectionString, "ADC_Conference", "MMCA-Conference");

        named.Should().NotBe(plain);
        new SqlConnectionStringBuilder(named).ApplicationName.Should().Be("MMCA-Conference");
        new SqlConnectionStringBuilder(named).InitialCatalog.Should().Be("ADC_Conference");
    }

    [Fact]
    public async Task DisposeAsync_RestoresTheFirstOriginalValue_OfEveryPushedKey()
    {
        const string key = "MMCA_TEST_CROSSSERVICE_RESTORE";
        const string absentKey = "MMCA_TEST_CROSSSERVICE_ABSENT";
        Environment.SetEnvironmentVariable(key, "original");

        try
        {
            var fixture = new FakeCrossServiceFixture();
            fixture.Push(key, "first");
            fixture.Push(key, "second");
            fixture.Push(absentKey, "pushed");

            Environment.GetEnvironmentVariable(key).Should().Be("second");
            Environment.GetEnvironmentVariable(absentKey).Should().Be("pushed");

            await fixture.DisposeAsync();

            Environment.GetEnvironmentVariable(key).Should().Be("original");
            Environment.GetEnvironmentVariable(absentKey).Should().BeNull();
            fixture.HostsDisposed.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable(absentKey, null);
        }
    }

    [Fact]
    public async Task SetHostConnectionString_PushesTheSharedTopLevelKey()
    {
        const string key = "ConnectionStrings__SQLServerConnectionString";
        var original = Environment.GetEnvironmentVariable(key);

        var fixture = new FakeCrossServiceFixture();
        try
        {
            fixture.PushHostConnectionString("Server=.;Database=Store_Sales;");

            Environment.GetEnvironmentVariable(key).Should().Be("Server=.;Database=Store_Sales;");
        }
        finally
        {
            await fixture.DisposeAsync();
            Environment.GetEnvironmentVariable(key).Should().Be(original);
        }
    }

    /// <summary>
    /// A subclass that never starts a container, so the environment and connection-string logic can be
    /// driven directly on a machine with no Docker daemon.
    /// </summary>
    private sealed class FakeCrossServiceFixture : CrossServiceFixtureBase
    {
        public bool HostsDisposed { get; private set; }

        protected override IReadOnlyList<CrossServiceDataSource> DataSources =>
            [new CrossServiceDataSource("Catalog", "Store_Catalog")];

        protected override string MigrationsAssemblyPrefix => "MMCA.Store.Migrations.SqlServer";

        public void Push(string key, string? value) => SetEnvironmentVariable(key, value);

        public void PushHostConnectionString(string connectionString) => SetHostConnectionString(connectionString);

        protected override ValueTask BootHostsAsync() => ValueTask.CompletedTask;

        protected override ValueTask DisposeHostsAsync()
        {
            HostsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
