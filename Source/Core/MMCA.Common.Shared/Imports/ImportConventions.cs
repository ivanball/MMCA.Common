namespace MMCA.Common.Shared.Imports;

/// <summary>
/// The shared conventions of a file-import endpoint (an upload read by <c>ReadCsvAsync</c> and answered
/// with an <see cref="ImportReportDTO"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Dry run.</b> The flag is a form or query field named <see cref="DryRunFieldName"/>, default
/// <see langword="false"/>. A dry run writes nothing and answers <c>200</c> with a report whose
/// <see cref="ImportReportDTO.DryRun"/> is <see langword="true"/>.
/// </para>
/// <para>
/// <b>Commit.</b> A synchronous commit answers <c>200</c> with a report whose
/// <see cref="ImportReportDTO.DryRun"/> is <see langword="false"/>; a queued commit answers <c>202</c>
/// with the application's own run DTO.
/// </para>
/// <para>
/// <b>File-level failure.</b> A malformed file, missing columns or a file over the size cap is a
/// <c>400</c> Problem Details, never a report: a report describes rows, and a file that cannot be read
/// has none. A row-level problem is an <see cref="ImportRowErrorDTO"/> in
/// <see cref="ImportReportDTO.RowErrors"/>.
/// </para>
/// </remarks>
public static class ImportConventions
{
    /// <summary>The form or query field that asks for a dry run.</summary>
    public const string DryRunFieldName = "dryRun";
}
