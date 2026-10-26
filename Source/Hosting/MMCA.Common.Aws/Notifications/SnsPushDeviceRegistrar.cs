using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Aws.Notifications;

/// <summary>
/// SNS mobile push implementation of <see cref="IPushDeviceRegistrar"/> (ADR-044). Each installation is
/// one SNS platform endpoint (subscribed to the broadcast topic) plus one row in the DynamoDB
/// registry, which is where ownership lives: the same handover and owner-scoped delete rules as the
/// Notification Hubs registrar, enforced by conditional writes.
/// <para>
/// Endpoints are created by device token, so two installation ids of one user holding the same
/// token (an app reinstall that minted a new id) share one endpoint. A delete therefore removes the
/// endpoint only when no other row of that user still points at it; the row itself always goes.
/// A token shared across two USERS' installation ids (a device that changed hands under a brand-new
/// installation id) is not detectable through the <c>UserId</c> index, so deleting one user's row
/// still removes the endpoint the other row points at; an <c>EndpointArn</c> index on the registry
/// would close that edge. Until then it self-heals: the surviving row's next send finds the
/// endpoint gone and drops the row, and the device re-registers on its next start.
/// </para>
/// </summary>
/// <param name="registry">The installation registry.</param>
/// <param name="sns">The SNS client.</param>
/// <param name="options">The push settings.</param>
/// <param name="logger">The logger.</param>
internal sealed class SnsPushDeviceRegistrar(
    SnsInstallationRegistry registry,
    IAmazonSimpleNotificationService sns,
    IOptions<SnsNativePushSettings> options,
    ILogger<SnsPushDeviceRegistrar> logger) : IPushDeviceRegistrar
{
    /// <inheritdoc />
    public async Task<Result> UpsertAsync(UserIdentifierType userId, DeviceInstallationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var platform = request.Platform.ToUpperInvariant() switch
        {
            "FCMV1" => DeviceInstallationRequest.FcmV1Platform,
            "APNS" => DeviceInstallationRequest.ApnsPlatform,
            _ => null,
        };
        if (platform is null)
        {
            return Result.Failure(Error.Validation(
                code: "PushDevice.UnsupportedPlatform",
                message: $"Platform must be '{DeviceInstallationRequest.FcmV1Platform}' or '{DeviceInstallationRequest.ApnsPlatform}'.",
                source: nameof(SnsPushDeviceRegistrar)));
        }

        var applicationArn = options.Value.PlatformApplicationArnFor(platform);
        if (applicationArn is null)
        {
            // A deployment may run one platform only (an Android-first app has no APNs application).
            return Result.Failure(Error.Validation(
                code: "PushDevice.UnsupportedPlatform",
                message: $"Platform '{platform}' is not enabled for push notifications on this deployment.",
                source: nameof(SnsPushDeviceRegistrar)));
        }

        try
        {
            return await RegisterAsync(SnsInstallationRegistry.UserKey(userId), platform, applicationArn, request, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonServiceException ex)
        {
            logger.LogError(ex, "Device installation upsert failed");
            return Result.Failure(Error.Failure(
                code: "PushDevice.UpsertFailed",
                message: "The device could not be registered for push notifications.",
                source: nameof(SnsPushDeviceRegistrar)));
        }
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(UserIdentifierType userId, string installationId, CancellationToken cancellationToken = default)
    {
        var owner = SnsInstallationRegistry.UserKey(userId);
        try
        {
            var existing = await registry.GetAsync(installationId, cancellationToken).ConfigureAwait(false);
            if (existing is null || !string.Equals(existing.UserId, owner, StringComparison.Ordinal))
            {
                // Unknown, or someone else's installation: reported as success either way. See the
                // interface remarks on the existence oracle.
                return Result.Success();
            }

            // Endpoint first, row last: a failure part-way leaves the row, so the client's retry finds
            // it and finishes the job, rather than an endpoint still subscribed to broadcasts with no
            // row left to find it by. The row delete is conditional on the owner, so a row handed over
            // in the meantime survives. The endpoint goes only when no other row of this user still
            // points at it (two installation ids holding one device token share an endpoint).
            var ownerRows = await registry.QueryByUserAsync(owner, cancellationToken).ConfigureAwait(false);
            var endpointShared = ownerRows.Any(row =>
                !string.Equals(row.InstallationId, installationId, StringComparison.Ordinal)
                && string.Equals(row.EndpointArn, existing.EndpointArn, StringComparison.Ordinal));
            if (!endpointShared)
            {
                await registry.RemoveEndpointAsync(existing.EndpointArn, existing.SubscriptionArn, cancellationToken).ConfigureAwait(false);
            }

            await registry.DeleteOwnedAsync(installationId, owner, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (AmazonServiceException ex)
        {
            logger.LogError(ex, "Device installation delete failed");
            return Result.Failure(Error.Failure(
                code: "PushDevice.DeleteFailed",
                message: "The device could not be unregistered from push notifications.",
                source: nameof(SnsPushDeviceRegistrar)));
        }
    }

    private static Result InstallationConflict() =>
        Result.Failure(Error.Conflict(
            code: "PushDevice.InstallationConflict",
            message: "The installation id is registered to another device. Generate a new installation id and retry.",
            source: nameof(SnsPushDeviceRegistrar)));

    /// <summary>
    /// Whether the installation exists, belongs to another user, AND points at a different push channel
    /// than the request: the same rule as the Notification Hubs registrar. An unknown id, the caller's
    /// own installation, or one whose channel the caller presents (the same device changing hands) is
    /// free for the caller to claim.
    /// </summary>
    private static bool ClaimedByAnotherDevice(SnsInstallation? existing, string owner, string pushChannel) =>
        existing is not null
        && !string.Equals(existing.UserId, owner, StringComparison.Ordinal)
        && !string.Equals(existing.PushChannel, pushChannel, StringComparison.Ordinal);

    private async Task<Result> RegisterAsync(string owner, string platform, string applicationArn, DeviceInstallationRequest request, CancellationToken cancellationToken)
    {
        var existing = await registry.GetAsync(request.InstallationId, cancellationToken).ConfigureAwait(false);

        // Answered before anything is created, so the other device's endpoint and row stay exactly as
        // they are. The conditional write below repeats the check atomically for a row that appears in
        // between.
        if (ClaimedByAnotherDevice(existing, owner, request.PushChannel))
        {
            return InstallationConflict();
        }

        var (endpointArn, createdEndpoint) = await EnsureEndpointAsync(existing, platform, applicationArn, request.PushChannel, cancellationToken).ConfigureAwait(false);

        var subscriptionArn = existing is not null && string.Equals(existing.EndpointArn, endpointArn, StringComparison.Ordinal)
            ? existing.SubscriptionArn
            : null;
        var createdSubscription = string.IsNullOrEmpty(subscriptionArn);
        if (createdSubscription)
        {
            subscriptionArn = (await sns.SubscribeAsync(
                new SubscribeRequest
                {
                    TopicArn = options.Value.BroadcastTopicArn,
                    Protocol = "application",
                    Endpoint = endpointArn,
                    ReturnSubscriptionArn = true,
                },
                cancellationToken).ConfigureAwait(false)).SubscriptionArn;
        }

        var installation = new SnsInstallation(request.InstallationId, owner, platform, request.PushChannel, endpointArn, subscriptionArn);
        if (!await registry.TryPutAsync(installation, cancellationToken).ConfigureAwait(false))
        {
            // Another device claimed the id between the read and the write. Undo what this request
            // created so no endpoint is left subscribed to broadcasts without a row to account for it.
            // Known edge: CreatePlatformEndpoint returns an existing endpoint for a token already
            // registered under a different installation id, which this removes too; that row's next
            // send then finds the endpoint gone and drops it, and its device re-registers on start.
            await RemoveUnaccountedAsync(
                createdEndpoint ? endpointArn : null,
                createdSubscription ? subscriptionArn : null,
                cancellationToken).ConfigureAwait(false);
            return InstallationConflict();
        }

        if (existing is not null && !string.Equals(existing.EndpointArn, endpointArn, StringComparison.Ordinal))
        {
            // The row moved to a new endpoint (the old one was gone, or the platform changed): the old
            // endpoint no longer has a row, so it must not keep receiving broadcasts.
            await RemoveUnaccountedAsync(existing.EndpointArn, existing.SubscriptionArn, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>
    /// Reuses the installation's endpoint when it is on the same platform (a token rotation, a
    /// re-registration, a handover); otherwise creates one. CreatePlatformEndpoint is idempotent per
    /// token, so a retried registration does not multiply endpoints.
    /// </summary>
    private async Task<(string EndpointArn, bool Created)> EnsureEndpointAsync(
        SnsInstallation? existing, string platform, string applicationArn, string pushChannel, CancellationToken cancellationToken)
    {
        if (existing is not null
            && string.Equals(existing.Platform, platform, StringComparison.Ordinal)
            && await TryRefreshEndpointAsync(existing.EndpointArn, pushChannel, cancellationToken).ConfigureAwait(false))
        {
            return (existing.EndpointArn, false);
        }

        var response = await sns.CreatePlatformEndpointAsync(
            new CreatePlatformEndpointRequest { PlatformApplicationArn = applicationArn, Token = pushChannel },
            cancellationToken).ConfigureAwait(false);
        return (response.EndpointArn, true);
    }

    /// <summary>
    /// Points an existing endpoint at the request's push channel and re-enables it (SNS disables an
    /// endpoint the platform reported invalid). Returns <see langword="false"/> when the endpoint no
    /// longer exists, so the caller creates a fresh one.
    /// </summary>
    private async Task<bool> TryRefreshEndpointAsync(string endpointArn, string pushChannel, CancellationToken cancellationToken)
    {
        try
        {
            await sns.SetEndpointAttributesAsync(
                new SetEndpointAttributesRequest
                {
                    EndpointArn = endpointArn,
                    Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Token"] = pushChannel,
                        ["Enabled"] = "true",
                    },
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (NotFoundException)
        {
            return false;
        }
    }

    /// <summary>Best-effort removal of an endpoint and subscription no row accounts for.</summary>
    private async Task RemoveUnaccountedAsync(string? endpointArn, string? subscriptionArn, CancellationToken cancellationToken)
    {
        try
        {
            if (endpointArn is not null)
            {
                await registry.RemoveEndpointAsync(endpointArn, subscriptionArn, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrEmpty(subscriptionArn))
            {
                await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subscriptionArn }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AmazonServiceException ex)
        {
            // The registration outcome stands; an orphaned endpoint is waste, not a correctness problem.
            logger.LogWarning(ex, "Removing an SNS endpoint no installation accounts for failed");
        }
    }
}
