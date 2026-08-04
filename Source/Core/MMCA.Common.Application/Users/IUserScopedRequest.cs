namespace MMCA.Common.Application.Users;

/// <summary>
/// A command or query addressed at a single user account. The shared Users use-case bases read the
/// target account only through this contract, so each app keeps its own command/query record (with
/// its own <c>ICacheInvalidating</c> choice and its own XML docs) and simply adds this interface.
/// </summary>
public interface IUserScopedRequest
{
    /// <summary>The account the command or query targets.</summary>
    UserIdentifierType UserId { get; }
}
