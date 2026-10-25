using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Configuration;

[assembly: AssemblyFixture(typeof(MMCA.Common.Gcp.Emulator.Tests.FakeGcsServerFixture))]

namespace MMCA.Common.Gcp.Emulator.Tests;

/// <summary>
/// One fake-gcs-server container for the whole tier, started once per assembly. fake-gcs-server
/// speaks the Cloud Storage JSON API (including resumable uploads) and has no Testcontainers module,
/// so it runs as a generic container. The image is pinned: an unpinned tag would turn an emulator
/// change into a red CI run nobody caused. It serves plain HTTP and checks no credentials, which is
/// exactly the posture the adapter takes for a plain-HTTP <c>GcsServiceUrl</c>.
/// </summary>
public sealed class FakeGcsServerFixture : IAsyncLifetime
{
    private const ushort Port = 4443;

    private readonly IContainer _container = new ContainerBuilder("fsouza/fake-gcs-server:1.56.1")
        .WithPortBinding(Port, assignRandomHostPort: true)
        .WithCommand("-scheme", "http", "-port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-backend", "memory")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(Port).ForPath("/storage/v1/b")))
        .Build();

    /// <summary>Gets the emulator endpoint as the host sees it, e.g. <c>http://127.0.0.1:49153/</c>.</summary>
    public Uri ServiceUrl => new UriBuilder(Uri.UriSchemeHttp, _container.Hostname, _container.GetMappedPublicPort(Port)).Uri;

    /// <summary>Builds a configuration from the given keys.</summary>
    /// <param name="settings">The configuration keys.</param>
    /// <returns>The configuration.</returns>
    public static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>The <c>FileStorage</c> keys pointing the Cloud Storage adapter at the emulator.</summary>
    /// <param name="bucket">The bucket name.</param>
    /// <returns>The configuration keys.</returns>
    public Dictionary<string, string?> FileStorageSettings(string bucket) => new()
    {
        ["FileStorage:GcsBucket"] = bucket,
        ["FileStorage:GcsServiceUrl"] = ServiceUrl.AbsoluteUri,
    };

    /// <summary>
    /// A Cloud Storage client on the emulator's JSON API without credentials, the posture the adapter
    /// takes for a plain-HTTP endpoint; the caller owns it.
    /// </summary>
    /// <returns>The client.</returns>
    public StorageClient CreateStorageClient() =>
        new StorageClientBuilder
        {
            BaseUri = $"{ServiceUrl.AbsoluteUri.TrimEnd('/')}/storage/v1/",
            UnauthenticatedAccess = true,
        }.Build();

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // A resumable upload continues at the URL the server hands back in its Location header,
        // which fake-gcs-server builds from its external URL. Inside the container that is its own
        // port, not the random host port Testcontainers mapped, so the fixture tells it the real one.
        // The public host is the same authority: fake-gcs-server serves the path-style object URL
        // the adapter returns ({endpoint}/{bucket}/{object}) only for a request addressed to it.
        using var http = new HttpClient();
        using var response = await http.PutAsJsonAsync(
            new Uri(ServiceUrl, "_internal/config"),
            new { externalUrl = ServiceUrl.AbsoluteUri.TrimEnd('/'), publicHost = ServiceUrl.Authority });
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
