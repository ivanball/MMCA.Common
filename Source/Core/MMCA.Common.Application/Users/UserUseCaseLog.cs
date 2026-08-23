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
}
