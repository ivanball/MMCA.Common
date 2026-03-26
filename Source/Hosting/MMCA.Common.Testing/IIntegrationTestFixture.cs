namespace MMCA.Common.Testing;

/// <summary>
/// Abstraction for integration test fixtures that manage the WebApplicationFactory lifecycle
/// and database reset between tests. Downstream projects implement this with their specific
/// application configuration.
/// </summary>
public interface IIntegrationTestFixture
{
    /// <summary>Creates a new <see cref="HttpClient"/> configured for the test server.</summary>
    HttpClient CreateClient();

    /// <summary>Resets the database to a clean state between tests (e.g., via Respawn).</summary>
    Task ResetDatabaseAsync();
}
