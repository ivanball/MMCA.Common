using System.Runtime.Serialization;

namespace MMCA.Common.Shared.Privacy;

/// <summary>
/// The portable data-subject export package (GDPR/CCPA access and portability): a snapshot of the
/// account itself plus one envelope per registered section of the user's data.
/// <para>
/// This document is <b>PII by design</b>. It exists to hand a data subject everything an app holds
/// about them, so it must only ever be produced for the account owner (or a privileged role) and
/// must never be logged, cached, or persisted by the pipeline that serves it.
/// </para>
/// </summary>
[DataContract]
public sealed record UserDataExportDTO
{
    /// <summary>
    /// Gets the version of the export document shape itself (not the app's data). Consumers read
    /// this before parsing, so a later change to the envelope can be detected rather than guessed.
    /// </summary>
    [DataMember(Order = 1)]
    public required string FormatVersion { get; init; }

    /// <summary>Gets the UTC instant the export was produced, from the injected time provider.</summary>
    [DataMember(Order = 2)]
    public required DateTimeOffset GeneratedOn { get; init; }

    /// <summary>Gets the user the export describes.</summary>
    [DataMember(Order = 3)]
    public required UserIdentifierType UserId { get; init; }

    /// <summary>
    /// Gets the app-specific snapshot of the account aggregate (the app's own subject DTO), or
    /// <see langword="null"/> when the app publishes no subject fields. Declared as
    /// <see cref="object"/> deliberately: the framework owns the envelope, each app owns which of
    /// its own fields are portable personal data, and a property typed <see cref="object"/>
    /// serializes by its runtime type under System.Text.Json.
    /// </summary>
    [DataMember(Order = 4)]
    public object? Subject { get; init; }

    /// <summary>
    /// Gets the section envelopes, in the order the sections were registered. A section that could
    /// not be produced is still present, reporting <c>Available = false</c>, so the reader can tell
    /// "no data" apart from "not retrieved".
    /// </summary>
    [DataMember(Order = 5)]
    public IReadOnlyList<UserDataExportSectionDTO> Sections { get; init; } = [];
}

/// <summary>
/// One section of a <see cref="UserDataExportDTO"/>: the data a single contributor holds for the
/// subject, plus whether that contributor could be reached at all.
/// </summary>
/// <remarks>
/// Sections are best-effort. When a contributor fails, the export still succeeds with this envelope
/// reporting <see cref="Available"/> false and a caller-safe <see cref="UnavailableReason"/>, so one
/// outage degrades one section rather than denying the data subject their whole export.
/// </remarks>
[DataContract]
public sealed record UserDataExportSectionDTO
{
    /// <summary>Gets the stable name identifying this section (e.g. "Engagement", "Sales").</summary>
    [DataMember(Order = 1)]
    public required string SectionName { get; init; }

    /// <summary>
    /// Gets whether the section was produced successfully. False means the section is incomplete and
    /// the export can be retried later; it does not mean the subject has no data here.
    /// </summary>
    [DataMember(Order = 2)]
    public required bool Available { get; init; }

    /// <summary>
    /// Gets the section payload (the contributor's own DTO), or <see langword="null"/> when the
    /// section is unavailable. Declared as <see cref="object"/> for the same reason
    /// <see cref="UserDataExportDTO.Subject"/> is: the payload shape is owned by the contributor.
    /// </summary>
    [DataMember(Order = 3)]
    public object? Data { get; init; }

    /// <summary>
    /// Gets a short, caller-safe explanation of why the section is unavailable, or
    /// <see langword="null"/> when it is available. Never carries exception messages, stack traces,
    /// connection strings, or peer addresses: this string is handed to the data subject.
    /// </summary>
    [DataMember(Order = 4)]
    public string? UnavailableReason { get; init; }
}
