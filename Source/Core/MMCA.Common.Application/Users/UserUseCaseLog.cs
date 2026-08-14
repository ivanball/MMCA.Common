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
}
