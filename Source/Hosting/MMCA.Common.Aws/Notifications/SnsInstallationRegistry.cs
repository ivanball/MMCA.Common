using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Aws.Notifications;

/// <summary>
/// The DynamoDB installation registry behind SNS mobile push (ADR-044). SNS has no ownership store of
/// its own (Notification Hubs keeps the owner as an installation tag), so this table holds it: one
/// item per installation id, the owner and push channel the ownership rule compares, and the endpoint
/// that delivers. Every ownership decision is a DynamoDB condition, so it holds under concurrency
/// without a lock.
/// </summary>
/// <param name="dynamoDb">The DynamoDB client.</param>
/// <param name="sns">The SNS client, for removing an installation's endpoint and subscription.</param>
/// <param name="options">The push settings (the table name).</param>
internal sealed class SnsInstallationRegistry(
    IAmazonDynamoDB dynamoDb,
    IAmazonSimpleNotificationService sns,
    IOptions<SnsNativePushSettings> options)
{
    /// <summary>
    /// The global secondary index user-targeted sends query: partition key <c>UserId</c> (string),
    /// projecting at least <c>EndpointArn</c> and <c>SubscriptionArn</c> (ALL is simplest).
    /// </summary>
    internal const string UserIdIndexName = "UserId-index";

    /// <summary>
    /// The write condition that expresses the ownership rule atomically: a new id, the caller's own
    /// installation, or another user's installation whose current push channel the caller presents
    /// (the same device changing hands). Anything else is another device's installation.
    /// </summary>
    internal const string OwnershipCondition = "attribute_not_exists(InstallationId) OR UserId = :me OR PushChannel = :channel";

    private const string InstallationIdAttribute = "InstallationId";
    private const string UserIdAttribute = "UserId";
    private const string PlatformAttribute = "Platform";
    private const string PushChannelAttribute = "PushChannel";
    private const string EndpointArnAttribute = "EndpointArn";
    private const string SubscriptionArnAttribute = "SubscriptionArn";

    private string Table => options.Value.InstallationsTable!;

    /// <summary>The invariant string form a user id is stored and indexed under.</summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The stored form.</returns>
    internal static string UserKey(UserIdentifierType userId) =>
        string.Create(CultureInfo.InvariantCulture, $"{userId}");

    /// <summary>Reads one installation (strongly consistent, since ownership decisions follow).</summary>
    /// <param name="installationId">The installation id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The installation, or <see langword="null"/> when the id is unknown.</returns>
    internal async Task<SnsInstallation?> GetAsync(string installationId, CancellationToken cancellationToken)
    {
        var response = await dynamoDb.GetItemAsync(
            new GetItemRequest
            {
                TableName = Table,
                Key = KeyOf(installationId),
                ConsistentRead = true,
            },
            cancellationToken).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? FromItem(item) : null;
    }

    /// <summary>Writes an installation under <see cref="OwnershipCondition"/>.</summary>
    /// <param name="installation">The installation, owned by the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="false"/> when the condition refused the write (another device's installation).</returns>
    internal async Task<bool> TryPutAsync(SnsInstallation installation, CancellationToken cancellationToken)
    {
        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [InstallationIdAttribute] = new() { S = installation.InstallationId },
            [UserIdAttribute] = new() { S = installation.UserId },
            [PlatformAttribute] = new() { S = installation.Platform },
            [PushChannelAttribute] = new() { S = installation.PushChannel },
            [EndpointArnAttribute] = new() { S = installation.EndpointArn },
        };
        if (!string.IsNullOrEmpty(installation.SubscriptionArn))
        {
            item[SubscriptionArnAttribute] = new() { S = installation.SubscriptionArn };
        }

        try
        {
            await dynamoDb.PutItemAsync(
                new PutItemRequest
                {
                    TableName = Table,
                    Item = item,
                    ConditionExpression = OwnershipCondition,
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":me"] = new() { S = installation.UserId },
                        [":channel"] = new() { S = installation.PushChannel },
                    },
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    /// <summary>Every installation a user owns, through <see cref="UserIdIndexName"/>, all pages.</summary>
    /// <param name="userKey">The user's stored form (<see cref="UserKey"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user's installations.</returns>
    internal async Task<IReadOnlyList<SnsInstallation>> QueryByUserAsync(string userKey, CancellationToken cancellationToken)
    {
        List<SnsInstallation> installations = [];
        Dictionary<string, AttributeValue>? startKey = null;
        while (true)
        {
            var response = await dynamoDb.QueryAsync(
                new QueryRequest
                {
                    TableName = Table,
                    IndexName = UserIdIndexName,
                    KeyConditionExpression = $"{UserIdAttribute} = :user",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":user"] = new() { S = userKey },
                    },
                    ExclusiveStartKey = startKey,
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var item in response.Items ?? [])
            {
                if (FromItem(item) is { } installation)
                {
                    installations.Add(installation);
                }
            }

            if (response.LastEvaluatedKey is not { Count: > 0 } next)
            {
                return installations;
            }

            startKey = next;
        }
    }

    /// <summary>
    /// Deletes an installation's row only while the given user still owns it, so a row handed over to
    /// another user in the meantime survives.
    /// </summary>
    /// <param name="installationId">The installation id.</param>
    /// <param name="userKey">The owner's stored form.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal Task DeleteOwnedAsync(string installationId, string userKey, CancellationToken cancellationToken) =>
        DeleteWhenAsync(installationId, $"{UserIdAttribute} = :me", ":me", userKey, cancellationToken);

    /// <summary>
    /// Deletes an installation's row only while it still points at the given endpoint, so a stale-endpoint
    /// cleanup never removes a registration refreshed onto a new endpoint in the meantime.
    /// </summary>
    /// <param name="installationId">The installation id.</param>
    /// <param name="endpointArn">The endpoint found stale.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal Task DeleteIfEndpointAsync(string installationId, string endpointArn, CancellationToken cancellationToken) =>
        DeleteWhenAsync(installationId, $"{EndpointArnAttribute} = :endpoint", ":endpoint", endpointArn, cancellationToken);

    /// <summary>
    /// Removes an endpoint from SNS: its broadcast subscription first (an already-removed one is the
    /// desired state), then the endpoint itself (idempotent on the SNS side).
    /// </summary>
    /// <param name="endpointArn">The endpoint.</param>
    /// <param name="subscriptionArn">Its topic subscription, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal async Task RemoveEndpointAsync(string endpointArn, string? subscriptionArn, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(subscriptionArn))
        {
            try
            {
                await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subscriptionArn }, cancellationToken).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                // Already gone: the desired state.
            }
        }

        await sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = endpointArn }, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteWhenAsync(string installationId, string condition, string placeholder, string value, CancellationToken cancellationToken)
    {
        try
        {
            await dynamoDb.DeleteItemAsync(
                new DeleteItemRequest
                {
                    TableName = Table,
                    Key = KeyOf(installationId),
                    ConditionExpression = condition,
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [placeholder] = new() { S = value },
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            // The row changed hands or endpoints since it was read: it is no longer this delete's to make.
        }
    }

    private static Dictionary<string, AttributeValue> KeyOf(string installationId) =>
        new(StringComparer.Ordinal) { [InstallationIdAttribute] = new() { S = installationId } };

    private static SnsInstallation? FromItem(Dictionary<string, AttributeValue> item)
    {
        var installationId = StringOf(item, InstallationIdAttribute);
        var userId = StringOf(item, UserIdAttribute);
        var endpointArn = StringOf(item, EndpointArnAttribute);
        if (installationId is null || userId is null || endpointArn is null)
        {
            // Not a row this registry wrote; nothing to deliver to or decide on.
            return null;
        }

        return new SnsInstallation(
            installationId,
            userId,
            StringOf(item, PlatformAttribute) ?? string.Empty,
            StringOf(item, PushChannelAttribute) ?? string.Empty,
            endpointArn,
            StringOf(item, SubscriptionArnAttribute));
    }

    private static string? StringOf(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value.S) ? value.S : null;
}
