using MMCA.Common.Application.Extensions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// The shared authentication workflow (login, registration, token refresh/rotation, revocation) the
/// app Identity modules previously duplicated (~70-95% line-identical). The workflow — validate-first,
/// ADR-029 lockout/rate-limit checks, the untracked-then-tracked dual-fetch, BR-205/206 refresh-token
/// rotation with reuse detection — lives once here; everything genuinely app-specific stays in the
/// sealed subclass via hooks:
/// <list type="bullet">
///   <item><see cref="FindUntrackedByEmailAsync"/> / <see cref="EmailExistsAsync"/> — the EF-translated
///     predicates are deliberately written against the app's concrete <c>User</c> (never an interface
///     member), so query translation is byte-for-byte what the app had before the hoist.</item>
///   <item><see cref="CreateUser"/> — the app's factory, default role and profile fields.</item>
///   <item><see cref="CreateAccessToken"/> — the app's claim set (e.g. <c>speaker_id</c> vs
///     <c>customer_id</c>) and display-name choice.</item>
///   <item><see cref="ValidateLoginCandidateAsync"/> / <see cref="ValidateRefreshCandidateAsync"/> — extra
///     gates such as a deactivated-account check.</item>
///   <item><see cref="OnUserRegisteredAsync"/> — the post-commit side-effect (publish an integration
///     event, or re-fetch to pick up a linked aggregate id written by a domain-event handler) returning
///     the instance to mint the first token from.</item>
/// </list>
/// <c>ExternalLoginAsync</c> stays app-level (the interface's default member rejects it), since OAuth
/// account linking is coupled to the app's <c>User</c> factory surface.
/// </summary>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
public abstract class AuthenticationServiceBase<TUser>(
    IUnitOfWork unitOfWork,
    ITokenService tokenService,
    IPasswordHasher passwordHasher,
    ILoginProtectionService loginProtection,
    TimeProvider timeProvider,
    AuthenticationValidators validators) : IAuthenticationService
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
    /// <summary>The unit of work (exposed for app-level workflows such as external login).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>The token service (exposed for app-level workflows such as external login).</summary>
    protected ITokenService TokenService => tokenService;

    /// <summary>The time provider (exposed for app-level workflows such as external login).</summary>
    protected TimeProvider TimeProvider => timeProvider;

    /// <summary>The user repository resolved from the unit of work.</summary>
    protected IRepository<TUser, UserIdentifierType> Repository =>
        unitOfWork.GetRepository<TUser, UserIdentifierType>();

    /// <summary>Access-token lifetime (BR-205 default: 15 minutes).</summary>
    protected virtual TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(15);

    /// <summary>Refresh-token lifetime (BR-205 default: 7 days).</summary>
    protected virtual TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(7);

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationResult = await validators.Login.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!validationResult.IsValid)
        {
            return Result.Failure<AuthenticationResponse>(validationResult.ToErrors(nameof(LoginAsync)));
        }

        // ADR-029 / BR-212: exponential-backoff lockout.
        var lockoutResult = await loginProtection.CheckLockoutAsync(request.Email, cancellationToken).ConfigureAwait(false);
        if (lockoutResult.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(lockoutResult.Errors);
        }

        // Normalize to the Email value object so the EF predicate compares same-typed converted
        // values (an invalid email yields a null VO that simply matches no user → invalid creds).
        var loginEmail = Email.Create(request.Email).Value;

        // Step 1: Untracked fetch — validate credentials without change-tracker overhead.
        // Soft-deleted accounts are excluded by EF query filters, returning the generic 401.
        var untracked = await FindUntrackedByEmailAsync(loginEmail, cancellationToken).ConfigureAwait(false);
        if (untracked is null)
        {
            await loginProtection.IncrementFailedAttemptsAsync(request.Email, cancellationToken).ConfigureAwait(false);
            return Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Auth.InvalidCredentials", "Invalid email or password.", nameof(LoginAsync)));
        }

        // App gate (e.g. deactivated-account rejection) — before password verification, no
        // failed-attempt increment (matches the pre-hoist behavior).
        var candidateResult = await ValidateLoginCandidateAsync(untracked, cancellationToken).ConfigureAwait(false);
        if (candidateResult.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(candidateResult.Errors);
        }

        if (!passwordHasher.VerifyPassword(request.Password, untracked.PasswordHash, untracked.PasswordSalt))
        {
            await loginProtection.IncrementFailedAttemptsAsync(request.Email, cancellationToken).ConfigureAwait(false);
            return Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Auth.InvalidCredentials", "Invalid email or password.", nameof(LoginAsync)));
        }

        // Step 2: Tracked re-fetch — needed to persist the new refresh token via SaveChangesAsync.
        var user = await Repository.GetByIdAsync(untracked.Id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure<AuthenticationResponse>(
                Error.NotFound.WithSource(nameof(LoginAsync)).WithTarget(typeof(TUser).Name));
        }

        // Reset failed attempts and lockout on successful login.
        await loginProtection.ResetFailedAttemptsAsync(request.Email, cancellationToken).ConfigureAwait(false);

        return await IssueTokensAsync(user, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> RegisterAsync(
        RegisterRequest request,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var validationResult = await validators.Register.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!validationResult.IsValid)
        {
            return Result.Failure<AuthenticationResponse>(validationResult.ToErrors(nameof(RegisterAsync)));
        }

        // ADR-029 / BR-213: IP-based registration rate limiting.
        var rateLimitResult = await loginProtection.CheckRegistrationRateLimitAsync(ipAddress, cancellationToken).ConfigureAwait(false);
        if (rateLimitResult.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(rateLimitResult.Errors);
        }

        var registerEmail = Email.Create(request.Email).Value;
        var emailExists = await EmailExistsAsync(registerEmail, cancellationToken).ConfigureAwait(false);
        if (emailExists)
        {
            return Result.Failure<AuthenticationResponse>(
                Error.Conflict("Auth.EmailAlreadyExists", "An account with this email already exists.", nameof(RegisterAsync)));
        }

        var (hash, salt) = passwordHasher.HashPassword(request.Password);
        var userResult = CreateUser(request, hash, salt);
        if (userResult.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(userResult.Errors);
        }

        var user = userResult.Value!;
        var refreshToken = tokenService.GenerateRefreshToken();
        user.UpdateRefreshToken(refreshToken, timeProvider.GetUtcNow().UtcDateTime.Add(RefreshTokenLifetime));

        await Repository.AddAsync(user, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Post-commit hook: publish the app's registration side-effect (integration event) and/or
        // re-fetch so the first token can carry an id written post-commit by a domain-event handler.
        var tokenUser = await OnUserRegisteredAsync(user, cancellationToken).ConfigureAwait(false);

        // BR-213: count this registration against the caller's IP.
        await loginProtection.IncrementRegistrationCountAsync(ipAddress, cancellationToken).ConfigureAwait(false);

        var accessToken = CreateAccessToken(tokenUser);

        return Result.Success(new AuthenticationResponse(
            accessToken,
            refreshToken,
            timeProvider.GetUtcNow().UtcDateTime.Add(AccessTokenLifetime)));
    }

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> RefreshTokenAsync(
        RefreshTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationResult = await validators.Refresh.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!validationResult.IsValid)
        {
            return Result.Failure<AuthenticationResponse>(validationResult.ToErrors(nameof(RefreshTokenAsync)));
        }

        // Extract claims from the expired JWT — signature validation still applies,
        // only the lifetime check is skipped.
        var principal = tokenService.GetPrincipalFromExpiredToken(request.AccessToken);
        if (principal is null)
        {
            return Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Auth.InvalidToken", "Invalid access token.", nameof(RefreshTokenAsync)));
        }

        var userIdClaim = principal.FindFirst("user_id")?.Value;
        if (!int.TryParse(userIdClaim, out var userId))
        {
            return Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Auth.InvalidToken", "Invalid access token claims.", nameof(RefreshTokenAsync)));
        }

        var user = await Repository.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure<AuthenticationResponse>(CreateRefreshUserMissingError());
        }

        // App gate (e.g. deactivated-account rejection).
        var candidateResult = await ValidateRefreshCandidateAsync(user, cancellationToken).ConfigureAwait(false);
        if (candidateResult.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(candidateResult.Errors);
        }

        // Verify token match + expiry. A mismatch indicates token reuse (potential theft).
        // BR-206: on reuse detection, revoke the stored token to force re-authentication.
        if (user.RefreshToken != request.RefreshToken || user.RefreshTokenExpiry < timeProvider.GetUtcNow().UtcDateTime)
        {
            user.RevokeRefreshToken();
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Auth.InvalidRefreshToken", "Invalid or expired refresh token.", nameof(RefreshTokenAsync)));
        }

        return await IssueTokensAsync(user, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result> RevokeTokenAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        var user = await Repository.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(Error.NotFound.WithSource(nameof(RevokeTokenAsync)).WithTarget(typeof(TUser).Name));
        }

        user.RevokeRefreshToken();
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Rotates the refresh token, persists, and returns the token-pair response. Shared by the
    /// login/refresh flows and reusable by app-level flows (e.g. external login).
    /// </summary>
    protected async Task<Result<AuthenticationResponse>> IssueTokensAsync(TUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var accessToken = CreateAccessToken(user);
        var refreshToken = tokenService.GenerateRefreshToken();
        user.UpdateRefreshToken(refreshToken, timeProvider.GetUtcNow().UtcDateTime.Add(RefreshTokenLifetime));

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(new AuthenticationResponse(
            accessToken,
            refreshToken,
            timeProvider.GetUtcNow().UtcDateTime.Add(AccessTokenLifetime)));
    }

    /// <summary>
    /// Fetches the user with the given email as a NO-TRACKING query, or null. Implement with a
    /// predicate on the app's concrete <c>User</c> (e.g. <c>u =&gt; u.Email == email</c>) so EF
    /// translation is identical to the pre-hoist code.
    /// </summary>
    protected abstract Task<TUser?> FindUntrackedByEmailAsync(Email? email, CancellationToken cancellationToken);

    /// <summary>
    /// Whether an account with this email already exists. The app decides whether soft-deleted
    /// accounts count (e.g. <c>ignoreQueryFilters: true</c> blocks re-registration of an erased email).
    /// </summary>
    protected abstract Task<bool> EmailExistsAsync(Email? email, CancellationToken cancellationToken);

    /// <summary>Creates the app's <c>User</c> via its domain factory (default role, profile fields).</summary>
    protected abstract Result<TUser> CreateUser(RegisterRequest request, byte[] passwordHash, byte[] passwordSalt);

    /// <summary>Mints the access token with the app's claim set and display-name choice.</summary>
    protected abstract string CreateAccessToken(TUser user);

    /// <summary>Extra login gate on the untracked candidate (default: none). Failures are returned as-is.</summary>
    protected virtual Task<Result> ValidateLoginCandidateAsync(TUser untrackedUser, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    /// <summary>Extra refresh gate on the fetched user (default: none). Failures are returned as-is.</summary>
    protected virtual Task<Result> ValidateRefreshCandidateAsync(TUser user, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());

    /// <summary>
    /// Post-commit registration side-effect; returns the instance the first access token is minted
    /// from (default: the tracked user unchanged).
    /// </summary>
    protected virtual Task<TUser> OnUserRegisteredAsync(TUser user, CancellationToken cancellationToken) =>
        Task.FromResult(user);

    /// <summary>
    /// The error returned when the refresh token's user no longer exists. Default: 401 Unauthorized
    /// (a token for a vanished user is indistinguishable from an invalid token); override to return
    /// 404 where the app's public contract already promises NotFound.
    /// </summary>
    protected virtual Error CreateRefreshUserMissingError() =>
        Error.Unauthorized("Auth.InvalidToken", "User not found.", nameof(RefreshTokenAsync));
}
