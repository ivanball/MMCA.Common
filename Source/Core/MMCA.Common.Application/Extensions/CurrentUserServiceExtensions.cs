using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Extensions;

/// <summary>
/// Extension members for <see cref="ICurrentUserService"/>.
/// </summary>
public static class CurrentUserServiceExtensions
{
    /// <summary>The message every app-side copy of the caller guard reports today.</summary>
    public const string AccessDeniedMessage = "Access denied.";

    extension(ICurrentUserService currentUserService)
    {
        /// <summary>
        /// Resolves the current user's identifier, or returns a failure when the request carries no
        /// authenticated user. Collapses the read-then-null-check-then-fail block that every
        /// handler and controller guarding a per-user operation repeats.
        /// </summary>
        /// <param name="code">
        /// The module's error code for a denied caller (e.g. "CheckIns.Forbidden"). Kept a
        /// parameter because the code names the module, which the framework cannot know.
        /// </param>
        /// <param name="message">The error message. Defaults to <see cref="AccessDeniedMessage"/>.</param>
        /// <param name="errorType">
        /// The error classification. Defaults to <see cref="ErrorType.Forbidden"/>, which is what
        /// the handler-side copies of this guard report; pass <see cref="ErrorType.Unauthorized"/>
        /// where the edge answers 401 instead.
        /// </param>
        /// <param name="source">Optional origin context, typically the calling handler's name.</param>
        /// <returns>
        /// A success result carrying the user identifier, or a failure carrying one error.
        /// </returns>
        public Result<UserIdentifierType> RequireUserId(
            string code,
            string message = AccessDeniedMessage,
            ErrorType errorType = ErrorType.Forbidden,
            string? source = null)
        {
            ArgumentNullException.ThrowIfNull(currentUserService);

            return currentUserService.UserId is { } userId
                ? Result.Success(userId)
                : Result.Failure<UserIdentifierType>(new Error(code, message, errorType, source));
        }
    }
}
