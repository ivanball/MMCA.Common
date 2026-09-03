using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.FeatureManagement.Mvc;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Users;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Privacy;

namespace MMCA.Common.API.Controllers.Privacy;

/// <summary>
/// Base controller for the data-subject export endpoint (GDPR/CCPA access and portability). It hands
/// the assembled package back as a downloadable JSON file rather than an inline body, because the
/// document exists to be saved and kept by the person it describes.
/// </summary>
/// <remarks>
/// <para>
/// A derived controller supplies the route and the app's query type, and nothing else:
/// </para>
/// <code>
/// [ApiController]
/// [Route("Users")]
/// [ApiVersion("1.0")]
/// public sealed class UsersDataExportController(
///     IQueryHandler&lt;ExportUserDataQuery, Result&lt;UserDataExportDTO&gt;&gt; handler,
///     ICurrentUserService currentUserService)
///     : DataExportControllerBase&lt;ExportUserDataQuery&gt;(handler, currentUserService)
/// {
///     protected override ExportUserDataQuery CreateQuery(
///         UserIdentifierType userId, UserIdentifierType currentUserId, string? currentUserRole) =&gt;
///         new(userId, currentUserId, currentUserRole);
/// }
/// </code>
/// <para>
/// The route lives on the subclass (the <c>AuthControllerBase</c> precedent) because the app owns its
/// URL space; the action template <c>{userId}/export</c> is fixed here, so a subclass routed at
/// <c>Users</c> serves the same <c>/Users/{userId}/export</c> path the hand-written controllers do.
/// </para>
/// <para>
/// The query type is app-owned (each app's <c>ExportUserDataQuery</c> lives in its own Application
/// assembly), which is why this ships as an abstract base with a <see cref="CreateQuery"/> factory
/// rather than as a concrete controller added through an application part: a concrete controller
/// could not construct a type it cannot see.
/// </para>
/// <para>
/// Authorization is defence in depth: the policy here requires an authenticated caller, and the
/// handler independently enforces owner-or-privileged-role, so the endpoint cannot leak another
/// subject's data even if the route is mounted without an <c>[Authorize]</c> of its own. The feature
/// gate keeps the whole surface off until a host turns <see cref="PrivacyFeatures.DataExport"/> on.
/// </para>
/// </remarks>
/// <typeparam name="TQuery">The app's export-user-data query record.</typeparam>
[Authorize]
[FeatureGate(PrivacyFeatures.DataExport)]
public abstract class DataExportControllerBase<TQuery>(
    IQueryHandler<TQuery, Result<UserDataExportDTO>> exportHandler,
    ICurrentUserService currentUserService) : ApiControllerBase
    where TQuery : IUserOwnedRequest
{
    /// <summary>The media type the export package is served as.</summary>
    public const string ExportContentType = "application/json";

    /// <summary>The current user service for this controller.</summary>
    protected ICurrentUserService CurrentUserService { get; } = currentUserService;

    /// <summary>
    /// Exports the personal data held for one user as a downloadable JSON document. The caller must
    /// be the account owner or hold the app's privileged role (enforced by the handler).
    /// </summary>
    /// <param name="userId">The user whose data is exported.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The export package as a file download, or a Problem Details failure.</returns>
    [HttpGet("{userId}/export")]
    [ProducesResponseType(typeof(UserDataExportDTO), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status403Forbidden, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public virtual async Task<IActionResult> ExportAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        var currentUserId = CurrentUserService.UserId;
        if (currentUserId is null)
        {
            return HandleFailure([Error.Unauthorized("Privacy.Unauthorized", "User is not authenticated.")]);
        }

        var query = CreateQuery(userId, currentUserId.Value, CurrentUserService.Role);
        Result<UserDataExportDTO> result = await exportHandler
            .HandleAsync(query, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return HandleFailure(result.Errors);
        }

        UserDataExportDTO export = result.Value!;

        // Serialized here rather than returned as an ObjectResult so the response really is a file:
        // Ok(export) would negotiate content and render inline, and the point of this endpoint is a
        // saved document. JsonSerializerOptions.Web keeps the payload byte-shape identical to every
        // other response this API produces.
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(export, JsonSerializerOptions.Web);

        return File(payload, ExportContentType, BuildFileName(userId, export.GeneratedOn));
    }

    /// <summary>
    /// Builds the app's query record from the route and the authenticated caller.
    /// </summary>
    /// <param name="userId">The user whose data is exported.</param>
    /// <param name="currentUserId">The authenticated caller.</param>
    /// <param name="currentUserRole">The authenticated caller's role claim; may be <see langword="null"/>.</param>
    /// <returns>The query to dispatch.</returns>
    protected abstract TQuery CreateQuery(
        UserIdentifierType userId,
        UserIdentifierType currentUserId,
        string? currentUserRole);

    /// <summary>
    /// The download file name: <c>user-data-{userId}-{yyyyMMdd}.json</c>. The date comes from the
    /// package's own <see cref="UserDataExportDTO.GeneratedOn"/> (the handler's time provider), so the
    /// file name and the document always agree, and the format is invariant so a saved file sorts and
    /// parses the same in every locale.
    /// </summary>
    /// <param name="userId">The exported user.</param>
    /// <param name="generatedOn">When the package was produced.</param>
    /// <returns>The file name offered in the Content-Disposition header.</returns>
    protected static string BuildFileName(UserIdentifierType userId, DateTimeOffset generatedOn) =>
        string.Create(CultureInfo.InvariantCulture, $"user-data-{userId}-{generatedOn:yyyyMMdd}.json");
}
