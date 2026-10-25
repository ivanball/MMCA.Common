using MMCA.Common.Gcp.Telemetry;

namespace MMCA.Common.Gcp.Tests.Telemetry;

/// <summary>
/// A token source that hands out a fixed access token (or throws, to model a host without
/// Application Default Credentials) and counts how often it was asked.
/// </summary>
internal sealed class FakeTelemetryTokenSource(string? token) : IGoogleCloudTelemetryTokenSource
{
    private int _calls;

    public int Calls => _calls;

    public Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return token is null
            ? Task.FromException<string>(new InvalidOperationException("No Application Default Credentials in this test."))
            : Task.FromResult(token);
    }
}
