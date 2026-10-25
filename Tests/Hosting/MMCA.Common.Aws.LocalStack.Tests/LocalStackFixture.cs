using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SimpleNotificationService;
using Amazon.SimpleSystemsManagement;
using Microsoft.Extensions.Configuration;
using Testcontainers.LocalStack;

[assembly: AssemblyFixture(typeof(MMCA.Common.Aws.LocalStack.Tests.LocalStackFixture))]

namespace MMCA.Common.Aws.LocalStack.Tests;

/// <summary>
/// One LocalStack container for the whole tier (S3, SQS, SNS, Secrets Manager, Systems Manager and DynamoDB),
/// started once per assembly. The image is pinned: LocalStack's later images need an auth token,
/// and an unpinned tag would turn that change into a red CI run nobody caused. LocalStack accepts any key pair, so the tier uses a
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

    /// <summary>The <c>SecretsManager</c> keys pointing the configuration source at LocalStack.</summary>
    /// <param name="namePrefix">A per-test secret name prefix, so tests never share a secret.</param>
    /// <returns>The configuration keys.</returns>
    public Dictionary<string, string?> SecretsManagerSettings(string namePrefix) => new()
    {
        ["SecretsManager:NamePrefix"] = namePrefix,
        ["SecretsManager:Region"] = Region,
        ["SecretsManager:ServiceUrl"] = ServiceUrl.AbsoluteUri,
        ["SecretsManager:AccessKeyId"] = DummyKey,
        ["SecretsManager:SecretAccessKey"] = DummyKey,
    };

    /// <summary>A Secrets Manager client on LocalStack, for arranging the secrets a test reads.</summary>
    /// <returns>The client; the caller disposes it.</returns>
    public AmazonSecretsManagerClient CreateSecretsManagerClient() =>
        new(new BasicAWSCredentials(DummyKey, DummyKey), new AmazonSecretsManagerConfig { ServiceURL = ServiceUrl.AbsoluteUri, AuthenticationRegion = Region });

    /// <summary>
    /// A Systems Manager client on LocalStack. A host registers its own client the same way when the
    /// AWS SDK defaults do not fit; <c>AddAwsDataProtection</c> then uses it as is.
    /// </summary>
    /// <returns>The client; the caller (or the container it is registered in) disposes it.</returns>
    public AmazonSimpleSystemsManagementClient CreateSsmClient() =>
        new(new BasicAWSCredentials(DummyKey, DummyKey), new AmazonSimpleSystemsManagementConfig { ServiceURL = ServiceUrl.AbsoluteUri, AuthenticationRegion = Region });

    /// <summary>The <c>NativePush</c> keys enabling SNS mobile push against LocalStack.</summary>
    /// <param name="table">The installations table.</param>
    /// <param name="topicArn">The broadcast topic.</param>
    /// <param name="fcmApplicationArn">The FCM platform application.</param>
    /// <param name="apnsApplicationArn">The APNs platform application, if any.</param>
    /// <returns>The configuration keys.</returns>
    public Dictionary<string, string?> NativePushSettings(string table, string topicArn, string fcmApplicationArn, string? apnsApplicationArn = null) => new()
    {
        ["NativePush:Enabled"] = "true",
        ["NativePush:Sns:InstallationsTable"] = table,
        ["NativePush:Sns:BroadcastTopicArn"] = topicArn,
        ["NativePush:Sns:FcmPlatformApplicationArn"] = fcmApplicationArn,
        ["NativePush:Sns:ApnsPlatformApplicationArn"] = apnsApplicationArn,
        ["NativePush:Sns:Region"] = Region,
        ["NativePush:Sns:ServiceUrl"] = ServiceUrl.AbsoluteUri,
        ["NativePush:Sns:AccessKeyId"] = DummyKey,
        ["NativePush:Sns:SecretAccessKey"] = DummyKey,
    };

    /// <summary>An SNS client on LocalStack, for arranging platform applications and topics and asserting endpoints.</summary>
    /// <returns>The client; the caller disposes it.</returns>
    public AmazonSimpleNotificationServiceClient CreateSnsClient() => new(Credentials, SnsConfig());

    /// <summary>The SNS client configuration pointing at LocalStack, for a test that builds its own client.</summary>
    /// <returns>The configuration.</returns>
    public AmazonSimpleNotificationServiceConfig SnsConfig() => new() { ServiceURL = ServiceUrl.AbsoluteUri, AuthenticationRegion = Region };

    /// <summary>Gets the dummy key pair LocalStack accepts.</summary>
    public static AWSCredentials Credentials => new BasicAWSCredentials(DummyKey, DummyKey);

    /// <summary>A DynamoDB client on LocalStack, for arranging the installations table and asserting its rows.</summary>
    /// <returns>The client; the caller disposes it.</returns>
    public AmazonDynamoDBClient CreateDynamoDbClient() =>
        new(new BasicAWSCredentials(DummyKey, DummyKey), new AmazonDynamoDBConfig { ServiceURL = ServiceUrl.AbsoluteUri, AuthenticationRegion = Region });

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => await _container.StartAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
