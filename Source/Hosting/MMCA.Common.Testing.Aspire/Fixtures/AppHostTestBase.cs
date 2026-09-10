using System.Data.Common;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Testing.Aspire.Probes;
using MMCA.Common.Testing.Support;

namespace MMCA.Common.Testing.Aspire.Fixtures;

/// <summary>
/// Base class for tests that run against a booted AppHost. It gives a test the two things it can
/// only get from the running application (an HTTP client bound to a named resource and a resolved
/// connection string) plus typed assertions for the wiring contracts the framework promises, so a
/// consumer's smoke tier states claims rather than re-implementing polling.
/// <para>
/// Skipping is the consuming project's call, not this base class's: check
/// <c>Fixture.IsAvailable</c> and skip with <c>Fixture.SkipReason</c>. This package deliberately
/// takes no dependency on the xUnit assertion library, so the skip API stays where the test project
/// already has it.
/// </para>
/// </summary>
/// <typeparam name="TFixture">The collection fixture that owns the running application.</typeparam>
/// <param name="fixture">The collection fixture.</param>
public abstract class AppHostTestBase<TFixture>(TFixture fixture)
    where TFixture : AppHostFixtureBase
{
    /// <summary>The collection fixture that owns the running application.</summary>
    protected TFixture Fixture { get; } = fixture;

    /// <summary>The running application.</summary>
    protected DistributedApplication Application => Fixture.Application;

    /// <summary>
    /// An <see cref="HttpClient"/> whose base address is a named resource's endpoint.
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="endpointName">The endpoint name, or <see langword="null"/> for the resource's single endpoint.</param>
    /// <returns>A client the caller disposes.</returns>
    protected HttpClient CreateHttpClient(string resourceName, string? endpointName = null) =>
        Application.CreateHttpClient(resourceName, endpointName);

    /// <summary>
    /// The connection string a connection-string resource (a database, a cache, a broker) resolves to
    /// now that the application is running.
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The resolved connection string, or <see langword="null"/> when the resource has none.</returns>
    protected async Task<string?> GetConnectionStringAsync(string resourceName, CancellationToken cancellationToken = default) =>
        await Application.GetConnectionStringAsync(resourceName, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Mints an RS256 bearer token the running stack will accept, signed with the collection's
    /// ephemeral private key when the fixture minted one and with the shared committed test key
    /// otherwise. The claim layout is the framework's own, so a request carrying this token reaches
    /// every reader the way a real one does.
    /// </summary>
    /// <param name="audience">The token audience, matching the host's <c>Jwt:Audience</c>.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="role">The role claim.</param>
    /// <param name="additionalClaims">Optional extra claims.</param>
    /// <param name="issuer">The issuer claim.</param>
    /// <param name="keyId">
    /// The JWT header <c>kid</c>. It must match the host's <c>Jwks:KeyId</c>, whose own fallback in
    /// <c>JwksSettings</c> is the literal "default" while the shared committed test key uses
    /// <c>mmca-test-key</c>.
    /// </param>
    /// <returns>The signed token.</returns>
    protected string CreateBearerToken(
        string audience,
        UserIdentifierType userId,
        string role,
        IEnumerable<Claim>? additionalClaims = null,
        string issuer = JwtTokenGenerator.DefaultIssuer,
        string keyId = JwtTokenGenerator.DefaultKeyId) =>
        JwtTokenGenerator.GenerateToken(
            audience,
            userId,
            role,
            additionalClaims,
            Fixture.E2eRsaKeys?.PrivateKeyPem,
            issuer,
            keyId);

    /// <summary>
    /// Asserts that a resource serves the full health report with 200. Every registered check takes
    /// part, so this is the strictest of the three probes and the one a human reads.
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="endpointName">The endpoint name, or <see langword="null"/> for the single endpoint.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>A task that completes when the assertion holds.</returns>
    protected async Task AssertHealthyAsync(
        string resourceName,
        string? endpointName = null,
        CancellationToken cancellationToken = default) =>
        await AssertProbeAsync(resourceName, AppHostProbePaths.Health, endpointName, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Asserts that a resource answers the LIVENESS probe with 200. This is the signal a startup gate
    /// is allowed to wait on: it answers as soon as the process can serve a request, so it cannot
    /// deadlock a dependency graph the way a readiness gate can.
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="endpointName">The endpoint name, or <see langword="null"/> for the single endpoint.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>A task that completes when the assertion holds.</returns>
    protected async Task AssertAliveAsync(
        string resourceName,
        string? endpointName = null,
        CancellationToken cancellationToken = default) =>
        await AssertProbeAsync(resourceName, AppHostProbePaths.Alive, endpointName, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Asserts that a resource answers the READINESS probe with 200, meaning traffic may be routed to
    /// it. Assert this about a service under test; never gate a startup wait on it.
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="endpointName">The endpoint name, or <see langword="null"/> for the single endpoint.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>A task that completes when the assertion holds.</returns>
    protected async Task AssertReadyAsync(
        string resourceName,
        string? endpointName = null,
        CancellationToken cancellationToken = default) =>
        await AssertProbeAsync(resourceName, AppHostProbePaths.Ready, endpointName, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Asserts that a resource publishes a key set containing at least one RSA signing key.
    /// <para>
    /// An empty key set is the failure mode worth naming: <c>RsaJwksProvider</c> answers
    /// <c>{"keys":[]}</c> rather than throwing whenever publishing is off or the key material is
    /// missing, so every peer's JwtBearer middleware fetches a document that validates nothing and
    /// the first cross-service call fails with an authentication error that points nowhere near the
    /// cause.
    /// </para>
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="endpointName">The endpoint name, or <see langword="null"/> for the single endpoint.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The raw key-set document, so a caller can assert more about it.</returns>
    protected async Task<string> AssertJwksAsync(
        string resourceName,
        string? endpointName = null,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateHttpClient(resourceName, endpointName);
        using var response = await client
            .GetAsync(new Uri(AppHostProbePaths.Jwks, UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "'{0}' must serve {1} anonymously",
            resourceName,
            AppHostProbePaths.Jwks);

        var document = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        CountRsaKeys(document).Should().BeGreaterThan(
            0,
            "'{0}' published a key set with no RSA key, so no peer can validate a token it signed",
            resourceName);

        return document;
    }

    /// <summary>
    /// Asserts that a resource's cleartext endpoint negotiates HTTP/2 with prior knowledge (h2c) and
    /// answers the probe path.
    /// <para>
    /// This is the contract every cross-service gRPC caller depends on and the one an ordinary HTTP
    /// assertion cannot express: a client that is allowed to downgrade proves nothing, because a
    /// listener speaking only HTTP/1.1 answers it perfectly well.
    /// </para>
    /// <para>
    /// <b>The target endpoint must be configured <c>HttpProtocols.Http2</c> ALONE.</b> A cleartext
    /// endpoint set to <c>Http1AndHttp2</c> does NOT serve h2c: with no TLS there is no ALPN to
    /// negotiate with, so Kestrel logs "HTTP/2 is not enabled for &lt;address&gt; ... Connections to
    /// this endpoint will use HTTP/1.1" and this assertion fails on the version check. Http2-only is
    /// the profile an extracted service runs (<c>ConfigureEndpointsWithHealthProbe</c> with
    /// <c>HttpProtocols.Http2</c>), and such a resource must be gated with the framework's
    /// <c>WithH2cHealthCheck()</c> rather than Aspire's stock HTTP probe, which speaks HTTP/1.1 and
    /// would never turn it healthy.
    /// </para>
    /// </summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="path">The path to GET. Defaults to the liveness probe, which every framework host serves on every listener.</param>
    /// <param name="endpointName">The cleartext endpoint name.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>A task that completes when the assertion holds.</returns>
    protected async Task AssertH2cAsync(
        string resourceName,
        string path = AppHostProbePaths.Alive,
        string endpointName = AppHostProbePaths.CleartextEndpointName,
        CancellationToken cancellationToken = default)
    {
        var endpoint = Application.GetEndpoint(resourceName, endpointName);

        using var response = await H2cProbe
            .SendAsync(new Uri(endpoint, path), cancellationToken)
            .ConfigureAwait(false);

        response.Version.Major.Should().Be(
            2,
            "'{0}' must answer {1} over HTTP/2 with prior knowledge on its cleartext endpoint",
            resourceName,
            path);
        response.IsSuccessStatusCode.Should().BeTrue(
            "'{0}' answered {1} over HTTP/2 with {2}",
            resourceName,
            path,
            response.StatusCode);
    }

    /// <summary>
    /// Asserts that a service resource carries a routable connection string for one logical data
    /// source, that is, that the AppHost really injected
    /// <c>DataSources__{logicalName}__&lt;Engine&gt;ConnectionString</c> and that the value parses.
    /// <para>
    /// <b>It stops at present and parseable, on purpose.</b> Opening the connection would mean
    /// carrying an ADO provider per engine (SQL Server, PostgreSQL, SQLite) inside a test-
    /// infrastructure package that has no other reason to know about any of them, and it would prove
    /// something already proven: the database resource has its own Aspire health check, and the
    /// fixture waited for the service that references it to become healthy, which it cannot do while
    /// its data source is unreachable. What is NOT otherwise proven, and what this asserts, is that
    /// the routing key the framework's multi-database resolver reads is the one the AppHost wrote:
    /// a typo in the logical name is invisible to a build and to every in-process test tier.
    /// </para>
    /// </summary>
    /// <param name="resourceName">The service resource name.</param>
    /// <param name="logicalName">The logical data source name, for example <c>Catalog</c>.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The injected connection string.</returns>
    protected async Task<string> AssertDataSourceAsync(
        string resourceName,
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var model = Application.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = model.Resources
            .OfType<IResourceWithEnvironment>()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, resourceName, StringComparison.Ordinal));

        resource.Should().NotBeNull("'{0}' must be a resource with environment variables", resourceName);

        // ExecutionConfigurationBuilder is the supported way to resolve a resource's environment now
        // that the older GetEnvironmentVariableValuesAsync helper is obsolete. It runs the same
        // environment callbacks the orchestrator ran, so endpoint and connection-string expressions
        // arrive resolved rather than as placeholders.
        var configuration = await ExecutionConfigurationBuilder
            .Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(Application.Services.GetRequiredService<DistributedApplicationExecutionContext>(), resourceLogger: null, cancellationToken)
            .ConfigureAwait(false);

        configuration.Exception.Should().BeNull("resolving the environment of '{0}' must not fail", resourceName);

        var variables = configuration.EnvironmentVariables;

        var prefix = $"DataSources__{logicalName}__";
        var match = variables.FirstOrDefault(entry =>
            entry.Key.StartsWith(prefix, StringComparison.Ordinal)
            && entry.Key.EndsWith("ConnectionString", StringComparison.Ordinal));

        match.Key.Should().NotBeNull(
            "'{0}' must carry a {1}<Engine>ConnectionString variable; the AppHost wires it with one of the With*DataSource extensions",
            resourceName,
            prefix);
        match.Value.Should().NotBeNullOrWhiteSpace(
            "'{0}' carries {1} with no value",
            resourceName,
            match.Key);

        // DbConnectionStringBuilder is the engine-agnostic parser: it rejects malformed key/value
        // pairs without knowing which provider will consume them.
        var parsed = new DbConnectionStringBuilder { ConnectionString = match.Value };
        parsed.Count.Should().BeGreaterThan(0, "{0} must parse into at least one keyword", match.Key);

        return match.Value;
    }

    /// <summary>Counts the RSA keys in a JWKS document, tolerating a malformed body as zero.</summary>
    /// <param name="document">The key-set document.</param>
    /// <returns>The number of keys whose <c>kty</c> is <c>RSA</c>.</returns>
    private static int CountRsaKeys(string document)
    {
        try
        {
            using var parsed = JsonDocument.Parse(document);
            if (!parsed.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            return keys.EnumerateArray().Count(key =>
                key.TryGetProperty("kty", out var keyType)
                && string.Equals(keyType.GetString(), "RSA", StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>GETs one probe path on a resource and asserts a 200.</summary>
    /// <param name="resourceName">The Aspire resource name.</param>
    /// <param name="path">The probe path.</param>
    /// <param name="endpointName">The endpoint name, or <see langword="null"/> for the single endpoint.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>A task that completes when the assertion holds.</returns>
    private async Task AssertProbeAsync(
        string resourceName,
        string path,
        string? endpointName,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(resourceName, endpointName);
        using var response = await client
            .GetAsync(new Uri(path, UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "'{0}' must answer {1} with 200",
            resourceName,
            path);
    }
}
