using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace MMCA.Common.Testing;

/// <summary>
/// Base class for integration tests providing HTTP helpers, lifecycle management,
/// and authentication utilities. Downstream test projects inherit and add
/// domain-specific authentication and entity creation helpers.
/// </summary>
/// <typeparam name="TFixture">The concrete fixture type implementing <see cref="IIntegrationTestFixture"/>.</typeparam>
public abstract class IntegrationTestBase<TFixture> : IAsyncLifetime
    where TFixture : IIntegrationTestFixture
{
    private static int _nextId = 1000;

    /// <summary>The test fixture managing the application lifetime and database.</summary>
    protected TFixture Fixture { get; }

    /// <summary>HTTP client configured for the test server.</summary>
    protected HttpClient Client { get; }

    protected IntegrationTestBase(TFixture fixture)
    {
        Fixture = fixture;
        Client = fixture.CreateClient();
    }

    /// <summary>Resets the database before each test.</summary>
    public async ValueTask InitializeAsync() => await Fixture.ResetDatabaseAsync();

    /// <summary>Disposes the HTTP client after each test.</summary>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        Client.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Sets the Authorization header to a Bearer token.</summary>
    protected void SetBearerToken(string token)
        => Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    /// <summary>Clears the Authorization header.</summary>
    protected void ClearAuthentication()
        => Client.DefaultRequestHeaders.Authorization = null;

    /// <summary>Sends a GET request and deserializes the response.</summary>
    protected async Task<T?> GetAsync<T>(string url)
    {
        var response = await Client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    /// <summary>Sends a POST request with a JSON body.</summary>
    protected async Task<HttpResponseMessage> PostAsync<T>(string url, T body) =>
        await Client.PostAsJsonAsync(url, body);

    /// <summary>Sends a PUT request with a JSON body.</summary>
    protected async Task<HttpResponseMessage> PutAsync<T>(string url, T body) =>
        await Client.PutAsJsonAsync(url, body);

    /// <summary>Sends a PUT request with no body.</summary>
    protected async Task<HttpResponseMessage> PutAsync(string url) =>
        await Client.PutAsync(url, null);

    /// <summary>Sends a DELETE request.</summary>
    protected async Task<HttpResponseMessage> DeleteAsync(string url) =>
        await Client.DeleteAsync(url);

    /// <summary>Thread-safe counter for generating unique test IDs.</summary>
    protected static int NextId() => Interlocked.Increment(ref _nextId);
}
