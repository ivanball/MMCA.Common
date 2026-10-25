namespace MMCA.Common.Aws.Notifications;

/// <summary>
/// One device installation in the DynamoDB registry: the client's installation id, its owner, the
/// platform and push channel it registered with, and the SNS endpoint (plus its broadcast topic
/// subscription) that delivers to it.
/// </summary>
/// <param name="InstallationId">The client-generated installation id, the table's partition key.</param>
/// <param name="UserId">The owning user, in its invariant string form (the index key).</param>
/// <param name="Platform">The normalized platform value (<c>fcmv1</c> or <c>apns</c>).</param>
/// <param name="PushChannel">The FCM registration token or APNs device token.</param>
/// <param name="EndpointArn">The SNS platform endpoint for the push channel.</param>
/// <param name="SubscriptionArn">The endpoint's subscription to the broadcast topic, if any.</param>
internal sealed record SnsInstallation(
    string InstallationId,
    string UserId,
    string Platform,
    string PushChannel,
    string EndpointArn,
    string? SubscriptionArn);
