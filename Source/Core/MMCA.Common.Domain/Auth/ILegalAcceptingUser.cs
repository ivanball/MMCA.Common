using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Auth;

/// <summary>
/// The Terms of Service acceptance an Identity module's <c>User</c> aggregate records: which terms
/// version the user agreed to, and when.
/// </summary>
/// <remarks>
/// <para>
/// Opting in is what lets an app record consent: an app stamps a version only on a user that
/// implements this interface, and an app that never configures a current terms version
/// (<c>LegalAcceptanceOptions.CurrentTermsVersion</c>) has nothing to stamp. Nothing else in the
/// framework changes shape.
/// </para>
/// <para>
/// <see cref="AcceptTerms"/> returns a <see cref="Result"/> rather than throwing so the aggregate
/// keeps the last word (the <see cref="IEmailConfirmableUser.ConfirmEmail"/> precedent).
/// </para>
/// </remarks>
public interface ILegalAcceptingUser
{
    /// <summary>The terms version the user last accepted, or <see langword="null"/> when they never accepted one.</summary>
    string? AcceptedTermsVersion { get; }

    /// <summary>The UTC instant the user last accepted the terms, or <see langword="null"/>.</summary>
    DateTime? TermsAcceptedOn { get; }

    /// <summary>
    /// Records that the user accepted <paramref name="version"/> at <paramref name="acceptedOn"/>.
    /// Implementations should be idempotent for the same version: accepting the version already on
    /// record is an ordinary repeat, not a fault.
    /// </summary>
    /// <param name="version">The terms version accepted. Must not be null or whitespace.</param>
    /// <param name="acceptedOn">The UTC instant of acceptance.</param>
    /// <returns>A success result, or the aggregate's invariant failure.</returns>
    Result AcceptTerms(string version, DateTime acceptedOn);
}
