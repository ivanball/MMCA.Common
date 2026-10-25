using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;

namespace MMCA.Common.Aws.Notifications;

/// <summary>
/// Builds the SNS and DynamoDB clients <c>AddAmazonSnsPush</c> registers, from
/// <see cref="SnsNativePushSettings"/>. Both point at the same endpoint (<see cref="AwsClientEndpoint.Apply"/>)
/// and sign with the same identity, since the registry and the endpoints it records are one store.
/// </summary>
internal static class SnsPushClientFactory
{
    /// <summary>Creates the SNS client.</summary>
    /// <param name="settings">The bound settings.</param>
    /// <param name="credentials">Static credentials, or <see langword="null"/> for the AWS default chain.</param>
    /// <returns>The SNS client.</returns>
    internal static IAmazonSimpleNotificationService CreateSns(SnsNativePushSettings settings, AWSCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var config = new AmazonSimpleNotificationServiceConfig();
        AwsClientEndpoint.Apply(config, settings.ServiceUrl, settings.Region);

        return credentials is null
            ? new AmazonSimpleNotificationServiceClient(config)
            : new AmazonSimpleNotificationServiceClient(credentials, config);
    }

    /// <summary>Creates the DynamoDB client.</summary>
    /// <param name="settings">The bound settings.</param>
    /// <param name="credentials">Static credentials, or <see langword="null"/> for the AWS default chain.</param>
    /// <returns>The DynamoDB client.</returns>
    internal static IAmazonDynamoDB CreateDynamoDb(SnsNativePushSettings settings, AWSCredentials? credentials)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var config = new AmazonDynamoDBConfig();
        AwsClientEndpoint.Apply(config, settings.ServiceUrl, settings.Region);

        return credentials is null
            ? new AmazonDynamoDBClient(config)
            : new AmazonDynamoDBClient(credentials, config);
    }
}
