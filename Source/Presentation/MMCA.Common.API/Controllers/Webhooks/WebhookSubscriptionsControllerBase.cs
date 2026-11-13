using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Idempotency;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Webhooks;

namespace MMCA.Common.API.Controllers.Webhooks;

/// <summary>
/// Base controller for the outbound webhook subscription endpoints: list, list the publishable event
/// types, create (the signing secret is shown once), update, rotate the secret (shown once), disable,
/// enable and delete. Every call is scoped to the caller's tenant.
/// </summary>
/// <remarks>
/// <para>
/// A derived controller supplies the route and the API version, and nothing else:
/// </para>
/// <code>
/// [ApiController]
/// [Route("Admin/WebhookSubscriptions")]
/// [ApiVersion("1.0")]
/// public sealed class WebhookSubscriptionsController : WebhookSubscriptionsControllerBase;
/// </code>
/// <para>
/// <b>Off means no routes.</b> Every action answers 404 unless the host called <c>AddWebhooks</c> AND
/// set <c>Webhooks:Enabled</c>; the service is resolved per action for that reason, since a host without
/// <c>AddWebhooks</c> has none to inject.
/// </para>
/// <para>
/// <b>Refusals a client has to expect:</b> <c>Webhooks.UrlInvalid</c>, <c>Webhooks.TargetNotAllowed</c>,
/// <c>Webhooks.EventTypesRequired</c>, <c>Webhooks.TooManyEventTypes</c>, <c>Webhooks.EventTypeUnknown</c>
/// and <c>Webhooks.DescriptionTooLong</c> (400), <c>Webhooks.NotFound</c> (404, also for another
/// tenant's id), and <c>Webhooks.LimitReached</c> (409).
/// </para>
/// </remarks>
[HasPermission(AdministrationPermissions.ManageWebhooks)]
public abstract class WebhookSubscriptionsControllerBase : ApiControllerBase
{
    /// <summary>The webhook service when the feature is enabled on this host, otherwise <see langword="null"/>.</summary>
    private IWebhookSubscriptionService? EnabledWebhooks =>
        HttpContext?.RequestServices.GetService<IWebhookSubscriptionService>() is { IsEnabled: true } webhooks ? webhooks : null;

    /// <summary>Lists the caller's subscriptions, disabled ones included; never their secrets.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscriptions.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<WebhookSubscriptionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual async Task<ActionResult<IReadOnlyList<WebhookSubscriptionResponse>>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (EnabledWebhooks is not { } webhooks)
        {
            return NotFound();
        }

        var result = await webhooks.ListAsync(cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Lists the webhook event types this host publishes (a subscription may also list <c>"*"</c>).</summary>
    /// <returns>The event type names.</returns>
    [HttpGet("event-types")]
    [ProducesResponseType(typeof(IReadOnlyCollection<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public virtual ActionResult<IReadOnlyCollection<string>> GetEventTypes() =>
        EnabledWebhooks is { } webhooks ? Ok(webhooks.EventTypes) : NotFound();

    /// <summary>Creates a subscription for the caller's tenant; the response carries the signing secret, once.</summary>
    /// <param name="request">The target URL, description and event types.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription and its signing secret.</returns>
    [HttpPost]
    [NonIdempotent("The response carries the only copy of the signing secret.")]
    [ProducesResponseType(typeof(WebhookSubscriptionSecretResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<WebhookSubscriptionSecretResponse>> CreateAsync(
        [FromBody] CreateWebhookSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EnabledWebhooks is not { } webhooks)
        {
            return NotFound();
        }

        var result = await webhooks.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Replaces a subscription's target URL, description and event types.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="request">The new values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription.</returns>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(WebhookSubscriptionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<WebhookSubscriptionResponse>> UpdateAsync(
        Guid id,
        [FromBody] UpdateWebhookSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (EnabledWebhooks is not { } webhooks)
        {
            return NotFound();
        }

        var result = await webhooks.UpdateAsync(id, request, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>
    /// Issues a new signing secret, returned once; the previous one keeps signing for
    /// <c>Webhooks:PreviousSecretGraceHours</c> so the receiver can switch without dropping deliveries.
    /// </summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription and its new secret.</returns>
    [HttpPost("{id:guid}/rotate-secret")]
    [NonIdempotent("The response carries the only copy of the new secret.")]
    [ProducesResponseType(typeof(WebhookSubscriptionSecretResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<WebhookSubscriptionSecretResponse>> RotateSecretAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (EnabledWebhooks is not { } webhooks)
        {
            return NotFound();
        }

        var result = await webhooks.RotateSecretAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Stops deliveries to a subscription. Disabling a disabled subscription succeeds.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription.</returns>
    [HttpPost("{id:guid}/disable")]
    [Idempotent]
    [ProducesResponseType(typeof(WebhookSubscriptionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<WebhookSubscriptionResponse>> DisableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (EnabledWebhooks is not { } webhooks)
        {
            return NotFound();
        }

        var result = await webhooks.DisableAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Resumes deliveries to a subscription.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subscription.</returns>
    [HttpPost("{id:guid}/enable")]
    [Idempotent]
    [ProducesResponseType(typeof(WebhookSubscriptionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<ActionResult<WebhookSubscriptionResponse>> EnableAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (EnabledWebhooks is not { } webhooks)
        {
            return NotFound();
        }

        var result = await webhooks.EnableAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : Ok(result.Value);
    }

    /// <summary>Soft-deletes a subscription; deliveries already queued for it complete without sending.</summary>
    /// <param name="id">The subscription id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204, or 404 for an unknown, deleted or other tenant's id.</returns>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (EnabledWebhooks is not { } webhooks)
        {
            return NotFound();
        }

        var result = await webhooks.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? HandleFailure(result.Errors) : NoContent();
    }
}
