using Microsoft.Extensions.Options;
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
/// <para>
/// <b>Refresh tokens are multi-device rows, hashed at rest.</b> Every issue opens a
/// <see cref="RefreshSession"/> instead of overwriting one column on the user, so signing in on a
/// second device no longer signs the first one out, and the store holds only
/// <see cref="RefreshSession.HashToken"/> digests. Rotation revokes the presented session and links it
/// to its successor; presenting an already-rotated token lands on that revoked row, which is the reuse
/// signal that revokes the user's whole live family (BR-206). An expired session is not a reuse signal
/// and fails alone. A per-user cap (<see cref="MaxActiveSessionsPerUser"/>) evicts the oldest live
/// session on a new sign-in so one account cannot grow the table without bound.
/// </para>
/// </summary>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
public abstract class AuthenticationServiceBase<TUser>(
    IUnitOfWork unitOfWork,
    ITokenService tokenService,
    IPasswordHasher passwordHasher,
    ILoginProtectionService loginProtection,
    TimeProvider timeProvider,
    AuthenticationValidators validators,
    IRefreshSessionStore refreshSessions,
    IOptions<RefreshSessionSettings>? refreshSessionSettings = null) : IAuthenticationService
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
    /// <summary>The cap applied when <c>RefreshSessions:MaxActiveSessionsPerUser</c> is not configured.</summary>
    private const int DefaultMaxActiveSessionsPerUser = 10;

    /// <summary>The unit of work (exposed for app-level workflows such as external login).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>The token service (exposed for app-level workflows such as external login).</summary>
    protected ITokenService TokenService => tokenService;

    /// <summary>The time provider (exposed for app-level workflows such as external login).</summary>
    protected TimeProvider TimeProvider => timeProvider;

    /// <summary>The refresh-session store (exposed for app-level workflows such as external login).</summary>
    protected IRefreshSessionStore RefreshSessions => refreshSessions;

    /// <summary>The user repository resolved from the unit of work.</summary>
    protected IRepository<TUser, UserIdentifierType> Repository =>
        unitOfWork.GetRepository<TUser, UserIdentifierType>();

    /// <summary>
    /// Access-token lifetime, from the token service (<c>Jwt:AccessTokenExpirationMinutes</c>) so the
    /// expiry reported to clients matches the JWT's actual <c>exp</c>. A non-positive value (a test
    /// double or a misconfigured host) falls back to the BR-205 default of 15 minutes.
    /// </summary>
    protected virtual TimeSpan AccessTokenLifetime =>
        tokenService.AccessTokenLifetime > TimeSpan.Zero ? tokenService.AccessTokenLifetime : TimeSpan.FromMinutes(15);

    /// <summary>
    /// Absolute refresh-token lifetime, from the token service (<c>Jwt:RefreshTokenExpirationDays</c>).
    /// A non-positive value (a test double or a misconfigured host) falls back to the BR-205 default
    /// of 7 days.
    /// </summary>
    protected virtual TimeSpan RefreshTokenLifetime =>
        tokenService.RefreshTokenLifetime > TimeSpan.Zero ? tokenService.RefreshTokenLifetime : TimeSpan.FromDays(7);

    /// <summary>
    /// Maximum live sessions one user may hold (<c>RefreshSessions:MaxActiveSessionsPerUser</c>,
    /// default 10). Opening session number cap + 1 revokes the user's oldest live session rather than
    /// refusing the sign-in.
    /// </summary>
    protected virtual int MaxActiveSessionsPerUser
    {
        get
        {
            // A non-positive value (an unbound options instance in a test double, or a host that
            // configured zero) falls back to the default rather than evicting every session.
            var configured = refreshSessionSettings?.Value.MaxActiveSessionsPerUser ?? 0;
            return configured > 0 ? configured : DefaultMaxActiveSessionsPerUser;
        }
    }

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> LoginAsync(
        LoginRequest request,
        string? ipAddress = null,
        string? userAgent = null,
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

        // Step 2: Tracked re-fetch. The refresh token no longer lives on the user, so this is no
        // longer about persisting one: it is the instance the app's CreateAccessToken hook mints
        // from (apps reach linked aggregates and navigations through it), and the second lookup is
        // what turns a race that deleted the account between the two steps into a clean 404.
        var user = await Repository.GetByIdAsync(untracked.Id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure<AuthenticationResponse>(
                Error.NotFound.WithSource(nameof(LoginAsync)).WithTarget(typeof(TUser).Name));
        }

        // Reset failed attempts and lockout on successful login.
        await loginProtection.ResetFailedAttemptsAsync(request.Email, cancellationToken).ConfigureAwait(false);

        return await IssueTokensAsync(user, ipAddress, userAgent, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> RegisterAsync(
        RegisterRequest request,
        string? ipAddress = null,
        string? userAgent = null,
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
            return EmailAlreadyExistsFailure();
        }

        var (hash, salt) = passwordHasher.HashPassword(request.Password);
        var userResult = CreateUser(request, hash, salt);
        if (userResult.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(userResult.Errors);
        }

        var user = userResult.Value!;

        await Repository.AddAsync(user, cancellationToken).ConfigureAwait(false);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Do not catch general exception types: the persistence exception is not visible from this layer (see below)
        catch (Exception)
#pragma warning restore CA1031
        {
            // The email lookup above is a check-then-act: two concurrent registrations for the same
            // address both pass it, and the loser only fails here, on the insert. Every consumer
            // puts a unique index on Email (ADC unfiltered, Store filtered on IsDeleted), so this
            // save is where the race actually surfaces, and without this catch it surfaces as a
            // generic 500 instead of the 409 a serialized pair of requests would have produced.
            //
            // The catch is deliberately broad: Application has no EF Core dependency (by layer
            // rule), so DbUpdateException is not a type this file can name. The re-check is what
            // narrows it. If the address exists now, the concurrent registration is the cause and
            // the caller gets the same conflict the serial path returns; anything else rethrows
            // untouched, so a genuine persistence fault still reaches the exception middleware.
            //
            // CancellationToken.None: the re-check has to run even when the caller's token is what
            // aborted the save, otherwise a cancelled save could never be classified.
            if (await EmailExistsAsync(registerEmail, CancellationToken.None).ConfigureAwait(false))
            {
                return EmailAlreadyExistsFailure();
            }

            throw;
        }

        // Post-commit hook: publish the app's registration side-effect (integration event) and/or
        // re-fetch so the first token can carry an id written post-commit by a domain-event handler.
        var tokenUser = await OnUserRegisteredAsync(user, cancellationToken).ConfigureAwait(false);

        // BR-213: count this registration against the caller's IP.
        await loginProtection.IncrementRegistrationCountAsync(ipAddress, cancellationToken).ConfigureAwait(false);

        // The session is opened AFTER the user save because it carries the user id, which a
        // store-generated key only has once the insert has run.
        return await IssueTokensAsync(tokenUser, ipAddress, userAgent, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<AuthenticationResponse>> RefreshTokenAsync(
        RefreshTokenRequest request,
        string? ipAddress = null,
        string? userAgent = null,
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

        // The identifier rides the standard `sub` claim; ClaimsPrincipalExtensions also accepts the
        // NameIdentifier form the bearer handler maps it to, and parses through IParsable so the
        // solution-wide identifier alias can change shape without editing this.
        var userId = principal.GetUserId();
        if (userId is null)
        {
            return Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Auth.InvalidToken", "Invalid access token claims.", nameof(RefreshTokenAsync)));
        }

        var user = await Repository.GetByIdAsync(userId.Value, cancellationToken).ConfigureAwait(false);
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

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sessionResult = await ResolveRotatableSessionAsync(user.Id, request.RefreshToken, now, cancellationToken)
            .ConfigureAwait(false);

        if (sessionResult.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(sessionResult.Errors);
        }

        var rotated = await RotateAsync(sessionResult.Value!, user.Id, now, ipAddress, userAgent, cancellationToken).ConfigureAwait(false);
        if (rotated.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(rotated.Errors);
        }

        return Result.Success(new AuthenticationResponse(
            CreateAccessToken(user),
            rotated.Value!,
            now.Add(AccessTokenLifetime)));
    }

    /// <inheritdoc />
    public async Task<Result> RevokeTokenAsync(
        UserIdentifierType userId,
        string? refreshToken = null,
        CancellationToken cancellationToken = default)
    {
        var user = await Repository.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(Error.NotFound.WithSource(nameof(RevokeTokenAsync)).WithTarget(typeof(TUser).Name));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var session = await refreshSessions
                .FindByTokenHashAsync(RefreshSession.HashToken(refreshToken), cancellationToken)
                .ConfigureAwait(false);

            // Only a live session of this user's identifies the device to sign out. Anything else
            // (unknown token, another account's token, an already-revoked row) leaves the caller
            // unidentifiable, so the request degrades to signing every device out rather than
            // reporting success for a revocation that reached nothing.
            if (session is not null
                && EqualityComparer<UserIdentifierType>.Default.Equals(session.UserId, userId)
                && !session.IsRevoked)
            {
                session.Revoke(now, RefreshSession.ReasonSignedOut);
                await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                return Result.Success();
            }
        }

        await RevokeLiveSessionsAsync(userId, RefreshSession.ReasonSignedOut, now, cancellationToken).ConfigureAwait(false);
        await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> RevokeAllSessionsAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        var user = await Repository.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure(Error.NotFound.WithSource(nameof(RevokeAllSessionsAsync)).WithTarget(typeof(TUser).Name));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await RevokeLiveSessionsAsync(userId, RefreshSession.ReasonSignedOut, now, cancellationToken).ConfigureAwait(false);
        await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// Opens a refresh session for the user, persists it, and returns the token-pair response. Shared
    /// by the login/registration flows and reusable by app-level flows (e.g. external login). The
    /// user's other sessions are untouched, except for the oldest one when the per-user cap is full.
    /// </summary>
    /// <param name="user">The authenticated user.</param>
    /// <param name="ipAddress">Optional client IP recorded on the new session.</param>
    /// <param name="userAgent">Optional client user-agent recorded on the new session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected async Task<Result<AuthenticationResponse>> IssueTokensAsync(
        TUser user,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var accessToken = CreateAccessToken(user);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var opened = await OpenSessionAsync(user.Id, now, ipAddress, userAgent, cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(opened.Errors);
        }

        await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(new AuthenticationResponse(
            accessToken,
            opened.Value!,
            now.Add(AccessTokenLifetime)));
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

    /// <summary>
    /// Resolves the session behind a presented refresh token and decides whether it may be rotated.
    /// The three rejections are deliberately different in what they do behind an identical error:
    /// an unknown hash (or one belonging to another account) says nothing about a live session and is
    /// failed alone, since revoking the family on it would let anyone holding one of this user's
    /// expired access tokens sign them out everywhere by posting a random token; a <b>revoked</b> row
    /// means this exact token was already rotated away or signed out and has come back, which is the
    /// BR-206 reuse signal that revokes every live session the user holds; an <b>expired</b> row is an
    /// ordinary end of life, so that device re-authenticates and the user's other devices keep working.
    /// </summary>
    private async Task<Result<RefreshSession>> ResolveRotatableSessionAsync(
        UserIdentifierType userId,
        string refreshToken,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Result.Failure<RefreshSession>(InvalidRefreshTokenError());
        }

        var session = await refreshSessions
            .FindByTokenHashAsync(RefreshSession.HashToken(refreshToken), cancellationToken)
            .ConfigureAwait(false);

        if (session is null || !EqualityComparer<UserIdentifierType>.Default.Equals(session.UserId, userId))
        {
            return Result.Failure<RefreshSession>(InvalidRefreshTokenError());
        }

        if (session.IsRevoked)
        {
            await RevokeLiveSessionsAsync(userId, RefreshSession.ReasonReuseDetected, now, cancellationToken)
                .ConfigureAwait(false);
            await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Result.Failure<RefreshSession>(InvalidRefreshTokenError());
        }

        return session.ExpiresAt <= now
            ? Result.Failure<RefreshSession>(InvalidRefreshTokenError())
            : Result.Success(session);
    }

    /// <summary>
    /// Mints a refresh token, opens its session, and stages the insert (without saving). Evicts the
    /// user's oldest live session first when the cap is already full.
    /// </summary>
    /// <returns>The plaintext refresh token to hand to the client.</returns>
    private async Task<Result<string>> OpenSessionAsync(
        UserIdentifierType userId,
        DateTime now,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var refreshToken = tokenService.GenerateRefreshToken();
        var sessionResult = RefreshSession.Create(
            userId,
            refreshToken,
            now,
            now.Add(RefreshTokenLifetime),
            ipAddress,
            userAgent);

        if (sessionResult.IsFailure)
        {
            return Result.Failure<string>(sessionResult.Errors);
        }

        await EnforceSessionCapAsync(userId, now, cancellationToken).ConfigureAwait(false);
        await refreshSessions.AddAsync(sessionResult.Value!, cancellationToken).ConfigureAwait(false);

        return Result.Success(refreshToken);
    }

    /// <summary>
    /// Revokes the presented session, links it to a freshly minted successor, and stages both
    /// (BR-205 rotation). Rotation replaces one session with one, so the cap is not re-evaluated here.
    /// </summary>
    /// <returns>The plaintext successor token to hand to the client.</returns>
    private async Task<Result<string>> RotateAsync(
        RefreshSession session,
        UserIdentifierType userId,
        DateTime now,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var refreshToken = tokenService.GenerateRefreshToken();
        var successorResult = RefreshSession.Create(
            userId,
            refreshToken,
            now,
            now.Add(RefreshTokenLifetime),
            ipAddress,
            userAgent);

        if (successorResult.IsFailure)
        {
            return Result.Failure<string>(successorResult.Errors);
        }

        var successor = successorResult.Value!;
        session.Revoke(now, RefreshSession.ReasonRotated, successor.TokenHash);
        await refreshSessions.AddAsync(successor, cancellationToken).ConfigureAwait(false);
        await refreshSessions.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(refreshToken);
    }

    /// <summary>Revokes every un-revoked session the user holds, without saving.</summary>
    private async Task RevokeLiveSessionsAsync(
        UserIdentifierType userId,
        string reason,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var sessions = await refreshSessions.GetUnrevokedByUserAsync(userId, cancellationToken).ConfigureAwait(false);
        foreach (var session in sessions)
        {
            session.Revoke(now, reason);
        }
    }

    /// <summary>
    /// Makes room for one more session: while the user is at or over the cap, revokes the oldest
    /// live one. Expired-but-unrevoked rows do not count against the cap (they authenticate nobody);
    /// they age out with the retention sweep the consumer schedules over the table.
    /// </summary>
    private async Task EnforceSessionCapAsync(UserIdentifierType userId, DateTime now, CancellationToken cancellationToken)
    {
        var cap = MaxActiveSessionsPerUser;
        var live = (await refreshSessions.GetUnrevokedByUserAsync(userId, cancellationToken).ConfigureAwait(false))
            .Where(s => s.IsActiveAt(now))
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToList();

        for (var index = 0; index <= live.Count - cap; index++)
        {
            live[index].Revoke(now, RefreshSession.ReasonSessionCap);
        }
    }

    /// <summary>
    /// The refresh rejection, shared by every failing branch so a caller cannot tell an unknown token
    /// from an expired one from a replayed one (the reuse case still revokes the family internally).
    /// </summary>
    private static Error InvalidRefreshTokenError() =>
        Error.Unauthorized("Auth.InvalidRefreshToken", "Invalid or expired refresh token.", nameof(RefreshTokenAsync));

    /// <summary>
    /// The registration conflict, returned both by the up-front email check and by the
    /// unique-index race recovery in <see cref="RegisterAsync"/> so the two paths are
    /// indistinguishable to the caller.
    /// </summary>
    private static Result<AuthenticationResponse> EmailAlreadyExistsFailure() =>
        Result.Failure<AuthenticationResponse>(
            Error.Conflict("Auth.EmailAlreadyExists", "An account with this email already exists.", nameof(RegisterAsync)));
}
