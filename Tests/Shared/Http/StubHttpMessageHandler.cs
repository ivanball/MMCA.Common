namespace MMCA.Common.Tests.Shared.Http;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers every request from a delegate and records each
/// request it was handed, so a probe test can drive the success, non-success, unreachable and
/// timed-out outcomes without a socket.
/// <para>
/// Linked as source into the test projects that use it (see the <c>Compile Include</c> in each
/// project file), so every project compiles its own internal copy.
/// </para>
/// </summary>
/// <param name="respond">
/// Produces the response for a request; throwing from it simulates a transport failure.
/// </param>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<ProbeAttempt> _attempts = [];

    // Recorded in order and BEFORE respond runs, so an attempt that throws still counts. This is
    // how a negotiation (HTTP/2 then HTTP/1.1) is told apart from a latched, single-request poll.
    public IReadOnlyList<ProbeAttempt> Attempts => _attempts;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _attempts.Add(new ProbeAttempt(request.Version, request.VersionPolicy));
        return Task.FromResult(respond(request));
    }
}

/// <summary>One request the check actually put on the wire, with the version profile it asked for.</summary>
/// <param name="Version">The HTTP version on the request.</param>
/// <param name="Policy">The version policy on the request.</param>
internal sealed record ProbeAttempt(Version Version, HttpVersionPolicy Policy);
