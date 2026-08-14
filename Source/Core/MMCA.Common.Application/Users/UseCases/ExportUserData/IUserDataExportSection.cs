namespace MMCA.Common.Application.Users.UseCases.ExportUserData;

/// <summary>
/// One contributor to a data-subject export: a module (or a cross-service peer client) that holds
/// personal data keyed by user and can hand it over on request.
/// </summary>
/// <remarks>
/// <para>
/// Sections are <b>best-effort</b>. The export handler wraps every call, so a section that throws
/// degrades to <c>Available = false</c> and the export still succeeds: one unreachable peer never
/// denies a data subject the rest of their data. A section that simply holds nothing for the user
/// returns <see cref="UserDataExportSectionResult.Complete"/> with an empty payload, which is a
/// truthful answer rather than an unknown one.
/// </para>
/// <para>
/// Register each implementation with <c>AddUserDataExportSection&lt;TSection&gt;()</c>; they
/// accumulate across modules and are exported in registration order.
/// </para>
/// </remarks>
public interface IUserDataExportSection
{
    /// <summary>
    /// Gets the stable name this section is published under in the export document (e.g.
    /// "Engagement", "Sales"). It appears verbatim in the package a data subject reads, so treat it
    /// as part of the contract rather than a label to reword.
    /// </summary>
    string SectionName { get; }

    /// <summary>
    /// Produces this section's data for one user.
    /// </summary>
    /// <param name="userId">The data subject.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The section result. Throwing is permitted (the handler degrades the section), but returning
    /// <see cref="UserDataExportSectionResult.Unavailable"/> is preferred where the reason is known.
    /// </returns>
    Task<UserDataExportSectionResult> ExportAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What one <see cref="IUserDataExportSection"/> hands back: its payload, or the fact that it could
/// not be produced.
/// </summary>
public sealed record UserDataExportSectionResult
{
    /// <summary>Gets the section name, echoed into the export envelope.</summary>
    public required string SectionName { get; init; }

    /// <summary>Gets whether the section was produced successfully.</summary>
    public required bool Available { get; init; }

    /// <summary>
    /// Gets the section payload: the contributor's own DTO. It must be JSON-serializable, because
    /// the export is handed to the data subject as a JSON document, and it is <b>PII by design</b>:
    /// never log it, never cache it.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// Gets a short, caller-safe explanation of why the section is unavailable. It reaches the data
    /// subject, so it must never carry internal exception details (messages, stack traces, peer
    /// addresses, connection strings).
    /// </summary>
    public string? UnavailableReason { get; init; }

    /// <summary>Creates a successfully produced section.</summary>
    /// <param name="sectionName">The section name.</param>
    /// <param name="data">The section payload (may be null when the section publishes no body).</param>
    /// <returns>An available section result.</returns>
    public static UserDataExportSectionResult Complete(string sectionName, object? data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        return new UserDataExportSectionResult
        {
            SectionName = sectionName,
            Available = true,
            Data = data,
        };
    }

    /// <summary>Creates a degraded section.</summary>
    /// <param name="sectionName">The section name.</param>
    /// <param name="reason">
    /// A short, caller-safe reason. Defaults to a generic message; do not pass exception text.
    /// </param>
    /// <returns>An unavailable section result.</returns>
    public static UserDataExportSectionResult Unavailable(string sectionName, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        return new UserDataExportSectionResult
        {
            SectionName = sectionName,
            Available = false,
            UnavailableReason = reason ?? UserDataExportSectionDefaults.UnavailableReason,
        };
    }
}

/// <summary>Shared defaults for degraded export sections.</summary>
public static class UserDataExportSectionDefaults
{
    /// <summary>
    /// The reason reported when a section fails and the app supplied none. Deliberately generic: the
    /// data subject learns the section is incomplete and retryable, and learns nothing about the
    /// internal failure.
    /// </summary>
    public const string UnavailableReason =
        "This section could not be retrieved. The data is unchanged; the export can be requested again later.";
}
