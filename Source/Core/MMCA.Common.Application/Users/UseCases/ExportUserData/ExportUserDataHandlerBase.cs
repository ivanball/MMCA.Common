using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Privacy;

namespace MMCA.Common.Application.Users.UseCases.ExportUserData;

/// <summary>
/// The shared data-subject export workflow (GDPR/CCPA access and portability): owner-or-privileged-role
/// authorization, a read-only load of the account, the app's own subject snapshot, then a best-effort
/// fan-out over every registered <see cref="IUserDataExportSection"/>. The result is one JSON-ready
/// package a data subject can be handed.
/// </summary>
/// <remarks>
/// <para>
/// Everything genuinely app-specific stays in the subclass:
/// <list type="bullet">
///   <item><see cref="HasExportPrivilege"/> - the role that bypasses ownership (Organizer vs Admin),
///     evaluated app-side because the role vocabulary stays app-owned.</item>
///   <item><see cref="BuildSubjectSnapshotAsync"/> - which of the account's own fields are portable
///     personal data. It is asynchronous, and receives the query, because an app may need to read a
///     second owned aggregate for the snapshot (Store's linked <c>Customer</c> profile does exactly
///     that).</item>
///   <item><see cref="OnExportCompletedAsync"/> - the app's tail (an access-log row, a metric), run
///     after the package is assembled and before it is returned.</item>
/// </list>
/// </para>
/// <para>
/// <b>Sections never fail the export.</b> Each registered section is invoked inside its own
/// try/catch: a throwing section is logged at warning level and degrades to an envelope reporting
/// <c>Available = false</c>, so one unreachable peer costs the subject one section rather than the
/// whole document. Section order is the registration order, so the package shape is stable across
/// runs. Cancellation is not degradation: an <see cref="OperationCanceledException"/> propagates.
/// </para>
/// <para>
/// This is a query handler, so it never calls <c>SaveChangesAsync</c>, and the account is fetched
/// through <c>GetReadRepository</c> rather than the read-write repository the two pre-hoist app
/// copies used: a no-tracking read is the correct choice for a projection both apps only read.
/// </para>
/// <para>
/// The document it produces is <b>PII by design</b>. It must never be logged or cached; the caching
/// query decorator is not applicable to it (the query implements no <c>IQueryCacheable</c>).
/// </para>
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <typeparam name="TQuery">The app's export-user-data query record.</typeparam>
public abstract class ExportUserDataHandlerBase<TUser, TQuery>(
    IUnitOfWork unitOfWork,
    IEnumerable<IUserDataExportSection> sections,
    TimeProvider timeProvider,
    ILogger logger) : IQueryHandler<TQuery, Result<UserDataExportDTO>>
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>
    where TQuery : IUserOwnedRequest
{
    /// <summary>
    /// The version stamped into <see cref="UserDataExportDTO.FormatVersion"/>. It versions the
    /// envelope only; an app changing its own subject or section payloads does not move it.
    /// </summary>
    public const string CurrentFormatVersion = "1.0";

    /// <summary>The unit of work (exposed so a subject-snapshot override can read further aggregates).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>
    /// The name reported as the <c>source</c> of any error this handler returns. Defaults to the
    /// runtime type name, so an app subclass that keeps the pre-hoist class name
    /// (<c>ExportUserDataHandler</c>) reports the identical error payload it did before.
    /// </summary>
    protected virtual string HandlerName => GetType().Name;

    /// <inheritdoc />
    public async Task<Result<UserDataExportDTO>> HandleAsync(
        TQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Authorization: owner or the app's privileged role (case-insensitive; claims may carry any casing).
        var forbidden = UserOwnershipRule.CheckOwnership(
            query,
            HasExportPrivilege(query.CurrentUserRole),
            code: "User.ExportForbidden",
            message: "You can only export your own account data.",
            source: HandlerName);
        if (forbidden is not null)
        {
            return Result.Failure<UserDataExportDTO>(forbidden);
        }

        var repository = unitOfWork.GetReadRepository<TUser, UserIdentifierType>();
        var user = await repository.GetByIdAsync(query.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure<UserDataExportDTO>(
                Error.NotFound.WithSource(HandlerName).WithTarget(typeof(TUser).Name));
        }

        var subject = await BuildSubjectSnapshotAsync(user, query, cancellationToken).ConfigureAwait(false);

        // Sequential on purpose: sections share the scoped unit of work and its DbContext, which is
        // not thread-safe, and registration order is the published order of the document.
        var envelopes = new List<UserDataExportSectionDTO>();
        foreach (var section in sections)
        {
            envelopes.Add(await RunSectionAsync(section, query.UserId, cancellationToken).ConfigureAwait(false));
        }

        var export = new UserDataExportDTO
        {
            FormatVersion = CurrentFormatVersion,
            GeneratedOn = timeProvider.GetUtcNow(),
            UserId = query.UserId,
            Subject = subject,
            Sections = envelopes,
        };

        await OnExportCompletedAsync(user, query, export, cancellationToken).ConfigureAwait(false);

        return Result.Success(export);
    }

    /// <summary>
    /// Whether the caller's role bypasses the ownership requirement (e.g.
    /// <c>UserRole.IsOrganizer(currentUserRole)</c> for ADC, <c>UserRole.IsAdmin(...)</c> for Store).
    /// </summary>
    /// <param name="currentUserRole">The caller's role claim; may be <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the caller may export any account.</returns>
    protected abstract bool HasExportPrivilege(string? currentUserRole);

    /// <summary>
    /// Projects the account itself into the app's portable subject snapshot: the fields the app
    /// considers personal data, deliberately excluding credentials (password hash and salt, refresh
    /// token, external-provider key), which are secrets rather than portable personal data.
    /// </summary>
    /// <param name="user">The loaded account.</param>
    /// <param name="query">The originating query (carries the caller, for apps that vary the snapshot).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The snapshot object, or <see langword="null"/> when the app publishes no subject fields. It
    /// must be JSON-serializable.
    /// </returns>
    protected abstract Task<object?> BuildSubjectSnapshotAsync(
        TUser user,
        TQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// The app's export tail, invoked once the package is assembled and before it is returned
    /// (default: nothing). Use it for an access-log row or a metric. It must not mutate the export,
    /// and a failure here is the app's to handle: the export has already succeeded.
    /// </summary>
    /// <param name="user">The loaded account.</param>
    /// <param name="query">The originating query.</param>
    /// <param name="export">The assembled package.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the tail is done.</returns>
    protected virtual Task OnExportCompletedAsync(
        TUser user,
        TQuery query,
        UserDataExportDTO export,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async Task<UserDataExportSectionDTO> RunSectionAsync(
        IUserDataExportSection section,
        UserIdentifierType userId,
        CancellationToken cancellationToken)
    {
        var sectionName = section.SectionName;

        try
        {
            var result = await section.ExportAsync(userId, cancellationToken).ConfigureAwait(false);

            return new UserDataExportSectionDTO
            {
                SectionName = result.SectionName,
                Available = result.Available,
                Data = result.Data,
                UnavailableReason = result.UnavailableReason,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: the contributor failed after whatever resilience pipeline it uses.
            // Degrade the section instead of failing the whole export. The reason handed back is
            // deliberately generic; the exception detail goes to the log, never to the subject.
            UserUseCaseLog.ExportSectionUnavailable(logger, ex, sectionName, userId);

            return new UserDataExportSectionDTO
            {
                SectionName = sectionName,
                Available = false,
                UnavailableReason = UserDataExportSectionDefaults.UnavailableReason,
            };
        }
    }
}
