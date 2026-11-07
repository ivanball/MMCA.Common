namespace MMCA.Common.Application.Auth.ViewAs;

/// <summary>
/// Reads the <see cref="ViewAsSubject"/> of an account. The consumer implements it over its own
/// <c>User</c> aggregate (the framework's <c>IAuthUser</c> carries passwords only), reading
/// soft-deleted rows too so a deleted account is refused rather than reported missing.
/// </summary>
public interface IViewAsSubjectResolver
{
    /// <summary>Returns the account's facts, or <see langword="null"/> when no such account exists.</summary>
    /// <param name="userId">The account to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subject, or <see langword="null"/>.</returns>
    Task<ViewAsSubject?> FindAsync(UserIdentifierType userId, CancellationToken cancellationToken = default);
}
