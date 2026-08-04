namespace MMCA.Common.Application.Users.UseCases.GetPreferences;

/// <summary>Query for the given user's stored UI preferences (ADR-027 / ADR-028).</summary>
/// <param name="UserId">The user whose preferences to read.</param>
public sealed record GetUserPreferencesQuery(UserIdentifierType UserId) : IUserScopedRequest;
