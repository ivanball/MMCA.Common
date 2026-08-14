using MMCA.Common.Application.Users;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;

namespace MMCA.Common.Application.Tests.Users;

/// <summary>
/// Minimal <c>User</c> aggregate standing in for an app's Identity user across the shared Users
/// use-case base tests. Public (not nested) because Moq must proxy <c>IRepository</c> closed over it.
/// </summary>
public class TestIdentityUser : AuditableAggregateRootEntity<UserIdentifierType>,
    IPasswordChangeableUser, IUserPreferences, IErasableUser
{
    public byte[] PasswordHash { get; private set; } = [1, 2, 3];

    public byte[] PasswordSalt { get; private set; } = [4, 5, 6];

    public string? RefreshToken { get; private set; }

    public DateTime? RefreshTokenExpiry { get; private set; }

    public string? PreferredCulture { get; private set; }

    public string? PreferredTheme { get; private set; }

    public bool AnonymizeCalled { get; private set; }

    public bool RevokeCalled { get; private set; }

    /// <summary>Forces the next <c>ChangePassword</c>/<c>UpdatePreferences</c>/<c>Anonymize</c> to fail.</summary>
    public Error? ForcedFailure { get; set; }

    public void SeedPreferences(string? culture, string? theme)
    {
        PreferredCulture = culture;
        PreferredTheme = theme;
    }

    public Result ChangePassword(byte[] newPasswordHash, byte[] newPasswordSalt)
    {
        if (ForcedFailure is { } error)
        {
            return Result.Failure(error);
        }

        PasswordHash = newPasswordHash;
        PasswordSalt = newPasswordSalt;
        return Result.Success();
    }

    public Result UpdatePreferences(string? preferredCulture, string? preferredTheme)
    {
        if (ForcedFailure is { } error)
        {
            return Result.Failure(error);
        }

        PreferredCulture = preferredCulture;
        PreferredTheme = preferredTheme;
        return Result.Success();
    }

    public Result Anonymize()
    {
        if (ForcedFailure is { } error)
        {
            return Result.Failure(error);
        }

        AnonymizeCalled = true;
        return Result.Success();
    }

    public void UpdateRefreshToken(string refreshToken, DateTime expiry)
    {
        RefreshToken = refreshToken;
        RefreshTokenExpiry = expiry;
    }

    public void RevokeRefreshToken()
    {
        RevokeCalled = true;
        RefreshToken = null;
        RefreshTokenExpiry = null;
    }
}

/// <summary>
/// A <c>User</c> that <b>hides</b> the base soft-delete with <c>public new Result Delete()</c>, the way
/// ADC's aggregate does to revoke the refresh token. Declaring <see cref="IErasableUser"/> here is what
/// re-maps the interface onto the hiding member, which is the property the shared erasure workflow
/// depends on.
/// </summary>
public sealed class TestHidingDeleteUser : TestIdentityUser, IErasableUser
{
    public bool HidingDeleteCalled { get; private set; }

    public new Result Delete()
    {
        HidingDeleteCalled = true;
        RevokeRefreshToken();
        return base.Delete();
    }
}

/// <summary>App-side change-password command shape (<c>UserId</c> plus the shared request payload).</summary>
public sealed record TestChangePasswordCommand(UserIdentifierType UserId, ChangePasswordRequest Request)
    : IUserScopedCommand<ChangePasswordRequest>;

/// <summary>App-side change-preferences command shape.</summary>
public sealed record TestChangePreferencesCommand(UserIdentifierType UserId, ChangePreferencesRequest Request)
    : IUserScopedCommand<ChangePreferencesRequest>;

/// <summary>App-side delete-user command shape (target plus authenticated caller).</summary>
public sealed record TestDeleteUserCommand(
    UserIdentifierType UserId,
    UserIdentifierType CurrentUserId,
    string? CurrentUserRole) : IUserOwnedRequest;

/// <summary>App-side export-user-data query shape (target plus authenticated caller).</summary>
public sealed record TestExportUserDataQuery(
    UserIdentifierType UserId,
    UserIdentifierType CurrentUserId,
    string? CurrentUserRole) : IUserOwnedRequest;
