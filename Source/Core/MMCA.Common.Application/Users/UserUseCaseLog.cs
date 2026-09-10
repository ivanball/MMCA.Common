using Microsoft.Extensions.Logging;

namespace MMCA.Common.Application.Users;

/// <summary>
/// The compile-time-generated log messages the shared Users use-case bases emit. Declared once in a
/// non-generic holder so every app subclass writes the same message text; the category still comes
/// from the <c>ILogger&lt;TApphandler&gt;</c> the subclass injects, so log filtering by handler keeps
/// working exactly as before the hoist.
/// </summary>
internal static partial class UserUseCaseLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} password changed")]
    internal static partial void PasswordChanged(ILogger logger, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} preferences changed")]
    internal static partial void PreferencesChanged(ILogger logger, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} account deleted and personal data anonymized")]
    internal static partial void UserErased(ILogger logger, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the soft-deleted marker for user {UserId}; the deleted user's existing access token stays usable until it expires")]
    internal static partial void SoftDeletedMarkerFailed(ILogger logger, UserIdentifierType userId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Data-subject export section {Section} unavailable for user {UserId}; export continues with Available=false")]
    internal static partial void ExportSectionUnavailable(ILogger logger, Exception exception, string section, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset requested for user {UserId}; reset email sent")]
    internal static partial void PasswordResetRequested(ILogger logger, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Password reset email could not be sent for user {UserId}; the issued token stays valid")]
    internal static partial void PasswordResetEmailFailed(ILogger logger, Exception exception, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset completed for user {UserId}")]
    internal static partial void PasswordResetCompleted(ILogger logger, UserIdentifierType userId);

    // No address and no account id: the reset endpoints answer identically whether or not the
    // address exists, and the log must not become the enumeration oracle the responses are not.
    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset request not actioned ({Reason})")]
    internal static partial void PasswordResetRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email confirmation requested for user {UserId}; confirmation email sent")]
    internal static partial void EmailConfirmationRequested(ILogger logger, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email confirmation could not be sent for user {UserId}; the issued token stays valid")]
    internal static partial void EmailConfirmationEmailFailed(ILogger logger, Exception exception, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email address confirmed for user {UserId}")]
    internal static partial void EmailConfirmed(ILogger logger, UserIdentifierType userId);

    // No address and no account id, for the reason the password-reset rejection carries neither: the
    // confirmation endpoints answer identically whether or not the address exists.
    [LoggerMessage(Level = LogLevel.Information, Message = "Email confirmation request not actioned ({Reason})")]
    internal static partial void EmailConfirmationRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Two-factor enrollment started for user {UserId}")]
    internal static partial void TwoFactorEnrollmentStarted(ILogger logger, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Two-factor authentication enabled for user {UserId}")]
    internal static partial void TwoFactorEnabled(ILogger logger, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Two-factor authentication disabled for user {UserId}")]
    internal static partial void TwoFactorDisabled(ILogger logger, UserIdentifierType userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Two-factor recovery codes regenerated for user {UserId}")]
    internal static partial void TwoFactorRecoveryCodesRegenerated(ILogger logger, UserIdentifierType userId);
}
