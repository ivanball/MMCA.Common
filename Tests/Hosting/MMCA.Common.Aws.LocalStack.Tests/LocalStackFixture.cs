using Microsoft.Extensions.Configuration;
using Testcontainers.LocalStack;

[assembly: AssemblyFixture(typeof(MMCA.Common.Aws.LocalStack.Tests.LocalStackFixture))]

namespace MMCA.Common.Aws.LocalStack.Tests;

/// <summary>
/// One LocalStack container for the whole tier (S3, SQS and SNS), started once per assembly. The
/// image is pinned: LocalStack's later images need an auth token, and an unpinned tag would turn
/// that change into a red CI run nobody caused. LocalStack accepts any key pair, so the tier uses a
/// fixed dummy one and never touches the AWS default credential chain.
/// </summary>
public sealed class LocalStackFixture : IAsyncLifetime
{
    /// <summary>The region every client in the tier signs for.</summary>
    public const string Region = "us-east-1";

    private const string DummyKey = "test";

    private readonly LocalStackContainer _container = new LocalStackBuilder("localstack/localstack:4.4").Build();

    /// <summary>Gets the LocalStack edge endpoint, e.g. <c>http://127.0.0.1:49153/</c>.</summary>
    public Uri ServiceUrl => new(_container.GetConnectionString());

    /// <summary>Builds a configuration from the given keys.</summary>
    /// <param name="settings">The configuration keys.</param>
    /// <returns>The configuration.</returns>
    public static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>The <c>FileStorage</c> keys pointing the S3 adapter at LocalStack, path-style.</summary>
    /// <param name="bucket">The bucket name.</param>
    /// <returns>The configuration keys.</returns>
    public Dictionary<string, string?> FileStorageSettings(string bucket) => new()
    {
        ["FileStorage:Bucket"] = bucket,
        ["FileStorage:Region"] = Region,
        ["FileStorage:ServiceUrl"] = ServiceUrl.AbsoluteUri,
        ["FileStorage:ForcePathStyle"] = "true",
        ["FileStorage:AccessKeyId"] = DummyKey,
        ["FileStorage:SecretAccessKey"] = DummyKey,
    };

    /// <summary>The <c>MessageBus</c> keys selecting the SQS transport against LocalStack.</summary>
    /// <param name="endpointPrefix">A per-test queue prefix, so tests never share a queue.</param>
    /// <returns>The configuration keys.</returns>
    public Dictionary<string, string?> MessageBusSettings(string endpointPrefix) => new()
    {
        ["MessageBus:Provider"] = "AmazonSqs",
        ["MessageBus:EndpointPrefix"] = endpointPrefix,
        ["MessageBus:AmazonSqs:Region"] = Region,
        ["MessageBus:AmazonSqs:ServiceUrl"] = ServiceUrl.AbsoluteUri,
        ["MessageBus:AmazonSqs:AccessKeyId"] = DummyKey,
        ["MessageBus:AmazonSqs:SecretAccessKey"] = DummyKey,
    };

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await _container.StartAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
