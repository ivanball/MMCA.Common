using Testcontainers.PostgreSql;

namespace MMCA.Common.Infrastructure.PostgreSQL.Tests;

/// <summary>One PostgreSQL container shared by every broker transport test of a class.</summary>
public sealed class PostgreSqlBrokerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    /// <summary>Gets the container's connection string.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();
}
