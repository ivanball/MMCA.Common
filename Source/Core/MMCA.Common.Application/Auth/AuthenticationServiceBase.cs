using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.EmailConfirmation;
using MMCA.Common.Application.Auth.Legal;
using MMCA.Common.Application.Auth.Sessions;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.Extensions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.ValueObjects.Contact;

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
/// <b>This class decides who is signed in; <see cref="IAuthSessionIssuer"/> decides what they are
/// handed.</b> Once a caller is proved, the multi-device refresh sessions (hashed at rest), BR-205
/// rotation with BR-206 reuse detection, the per-user session cap and the <c>sid</c>/<c>mfa</c> claims
/// on the minted access token are the issuer's job, so this workflow never touches a session row.
/// </para>
/// </summary>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
/// <param name="unitOfWork">The unit of work the user aggregate is loaded and saved through.</param>
/// <param name="passwordHasher">Verifies and derives credential material.</param>
/// <param name="loginProtection">The ADR-029 lockout and registration rate limiter.</param>
/// <param name="validators">The request validators for login, registration and refresh.</param>
/// <param name="sessionIssuer">Issues, rotates and revokes the access/refresh pair behind each device.</param>
/// <param name="twoFactor">
/// Optional second-factor challenge. Supplied only by an app that has adopted two-factor
/// authentication; while it is null the sign-in flow has no second-factor step at all and behaves
/// exactly as it did before the feature shipped. Optional and defaulted so every existing subclass
/// keeps compiling untouched (the <c>ChangePasswordHandlerBase</c> precedent).
/// </param>
/// <param name="emailConfirmationSettings">
/// Optional email-confirmation options. Supplied only by an app that has adopted confirmation, and
/// even then the sign-in gate stays off until <c>RequireConfirmedEmail</c> is set AND the app's
/// <c>User</c> implements <see cref="IEmailConfirmableUser"/>.
/// </param>
public abstract class AuthenticationServiceBase<TUser>(
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    ILoginProtectionService loginProtection,
    AuthenticationValidators validators,
    IAuthSessionIssuer sessionIssuer,
    ITwoFactorAuthenticator? twoFactor = null,
    IOptions<EmailConfirmationSettings>? emailConfirmationSettings = null) : IAuthenticationService
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>, IAuthUser
{
    /// <summary>
    /// The second-factor method that satisfied THIS request's challenge, or null. Armed by
    /// <see cref="LoginAsync"/> before the token is minted and carried over by
    /// <see cref="RefreshTokenAsync"/> from the presented token, so a rotation does not silently drop
    /// a step-up the user already performed.
    /// </summary>
    /// <remarks>
    /// A plain field for the same reason the issuer's session arming is one: this service is scoped,
    /// resolved per request, and one request issues one token pair.
    /// </remarks>
    private string? _multiFactorMethod;

    /// <summary>The unit of work (exposed for app-level workflows such as external login).</summary>
    protected IUnitOfWork UnitOfWork => unitOfWork;

    /// <summary>
    /// The token service (exposed for app-level workflows such as external login).
    /// <para>
    /// Minting through this property rather than through an injected <see cref="ITokenService"/> is
    /// what puts the <c>sid</c> claim on the token: while the workflow is issuing or rotating a
    /// session, this instance (the <see cref="IAuthSessionIssuer.TokenService"/>) stamps that
    /// session's id onto every access token it mints. A subclass that mints from its own
    /// <see cref="ITokenService"/> reference still produces a valid token, just one with no <c>sid</c>.
    /// </para>
    /// </summary>
    protected ITokenService TokenService => sessionIssuer.TokenService;

    /// <summary>The user repository resolved from the unit of work.</summary>
    protected IRepository<TUser, UserIdentifierType> Repository =>
        unitOfWork.GetRepository<TUser, UserIdentifierType>();

    /// <summary>
    /// The Terms of Service version the host currently requires, or <see langword="null"/> when terms
    /// acceptance is not configured. Read from <see cref="AuthenticationValidators.CurrentTermsVersion"/>
    /// (the registration parameter object), so adopting the feature adds no constructor dependency
    /// here or in the app's subclass: the host calls <c>AddLegalAcceptance(configuration)</c> and sets
    /// <c>Legal:CurrentTermsVersion</c>.
    /// <para>
    /// When it is non-null, <see cref="RegisterAsync"/> has already refused any request whose
    /// <see cref="RegisterRequest.AcceptedTerms"/> is false by the time <see cref="CreateUser"/> runs,
    /// so a <see cref="CreateUser"/> override stamps this version on the new user (for example
    /// <c>user.AcceptTerms(CurrentTermsVersion, now)</c> on an <see cref="ILegalAcceptingUser"/>).
    /// An external-login path that creates users outside <see cref="RegisterAsync"/> leaves them
    /// unstamped, and the UI's acceptance gate asks them on first sign-in.
    /// </para>
    /// </summary>
    protected string? CurrentTermsVersion => validators.CurrentTermsVersion;

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

        // SECURITY: an account with no stored credential material (the shape an external-OAuth
        // account carries, ADR-036) can never be reached by password login. It is answered exactly
        // like an unknown address, cost included, so the two are indistinguishable.
        if (untracked is null || !HasStoredCredential(untracked))
        {
            BurnPasswordVerificationCost(request.Password);
            await loginProtection.IncrementFailedAttemptsAsync(request.Email, cancellationToken).ConfigureAwait(false);
            return Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Auth.InvalidCredentials", "Invalid email or password.", nameof(LoginAsync)));
        }

        if (!passwordHasher.VerifyPassword(request.Password, untracked.PasswordHash, untracked.PasswordSalt))
        {
            await loginProtection.IncrementFailedAttemptsAsync(request.Email, cancellationToken).ConfigureAwait(false);
            return Result.Failure<AuthenticationResponse>(
                Error.Unauthorized("Auth.InvalidCredentials", "Invalid email or password.", nameof(LoginAsync)));
        }

        // App gate (e.g. deactivated-account rejection) runs AFTER the password check on purpose:
        // reaching it proves the caller owns the account, so the gate's distinct status message is
        // told to the owner rather than to anyone sweeping addresses. Running it first made account
        // state readable with no credential at all, and skipped the failed-attempt counter.
        var candidateResult = await ValidateLoginCandidateAsync(untracked, cancellationToken).ConfigureAwait(false);
        if (candidateResult.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(candidateResult.Errors);
        }

        // Both gates below run AFTER the password check, for the same reason the app gate does: they
        // return distinct, actionable errors ("confirm your address", "send a code"), and reaching
        // them proves the caller owns the account, so neither is readable by anyone sweeping
        // addresses.
        var confirmationResult = CheckEmailConfirmed(untracked);
        if (confirmationResult.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(confirmationResult.Errors);
        }

        var secondFactor = await ChallengeSecondFactorCountingFailuresAsync(untracked.Id, request, cancellationToken)
            .ConfigureAwait(false);
        if (secondFactor.IsFailure)
        {
            return Result.Failure<AuthenticationResponse>(secondFactor.Errors);
        }

        // Step 2: Tracked re-fetch. Refresh tokens live in their own session rows, so this fetch is
        // purely about the instance the app's CreateAccessToken hook mints from (apps reach linked
        // aggregates and navigations through it), and the second lookup is what turns a race that
        // deleted the account between the two steps into a clean 404.
        var user = await Repository.GetByIdAsync(untracked.Id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Result.Failure<AuthenticationResponse>(
                Error.NotFound.WithSource(nameof(LoginAsync)).WithTarget(typeof(TUser).Name));
        }

        // Reset failed attempts and lockout on successful login.
        await loginProtection.ResetFailedAttemptsAsync(request.Email, cancellationToken).ConfigureAwait(false);

        _multiFactorMethod = MultiFactorMethodFor(secondFactor.Value);
        try
        {
            return await IssueTokensAsync(user, ipAddress, userAgent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _multiFactorMethod = null;
        }
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

        // Terms of Service: only when the host configured a current version. The client sends a
        // plain flag (the anonymous register page cannot read the version); CreateUser stamps the
        // configured version through CurrentTermsVersion.
        if (CurrentTermsVersion is not null && !request.AcceptedTerms)
        {
            return Result.Failure<AuthenticationResponse>(LegalAcceptanceErrors.TermsNotAccepted(nameof(RegisterAsync)));
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
        var principal = TokenService.GetPrincipalFromExpiredToken(request.AccessToken);
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

        // The step-up the user already performed is carried across the rotation. The claim is read off
        // the presented access token, whose SIGNATURE was validated above (only its lifetime was
        // skipped), so this is the framework's own assertion coming back rather than caller input.
        // Dropping it instead would quietly demote a signed-in session every fifteen minutes and make
        // every IRequiresMfa use case unreachable without a fresh sign-in.
        _multiFactorMethod = principal.FindMultiFactorMethod();
        try
        {
            // The issuer resolves the presented token (reuse detection included) and mints the
            // successor's access token through the hook, so the new `sid` follows the rotation.
            return await sessionIssuer.RotateAsync(
                user.Id,
                request.RefreshToken,
                sessionId => CreateAccessTokenForSession(user, sessionId),
                ipAddress,
                userAgent,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _multiFactorMethod = null;
        }
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

        await sessionIssuer.SignOutAsync(userId, refreshToken, cancellationToken).ConfigureAwait(false);

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

        await sessionIssuer.SignOutEverywhereAsync(userId, cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>No user lookup is involved, so a list is one query (see <see cref="IAuthSessionIssuer.ListActiveAsync"/>).</remarks>
    public async Task<Result<IReadOnlyList<RefreshSessionSummaryResponse>>> GetSessionsAsync(
        UserIdentifierType userId,
        Guid? currentSessionId = null,
        CancellationToken cancellationToken = default)
    {
        var summaries = await sessionIssuer.ListActiveAsync(userId, currentSessionId, cancellationToken).ConfigureAwait(false);

        return Result.Success(summaries);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Ownership is checked by the issuer's store query, so another account's session id and an id
    /// that never existed produce the same <c>NotFound</c> (see
    /// <see cref="IAuthSessionIssuer.RevokeSessionAsync"/>).
    /// </remarks>
    public Task<Result> RevokeSessionByIdAsync(
        UserIdentifierType userId,
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        sessionIssuer.RevokeSessionAsync(userId, sessionId, cancellationToken);

    /// <summary>
    /// Opens a refresh session for the user, persists it, and returns the token-pair response. Shared
    /// by the login/registration flows and reusable by app-level flows (e.g. external login). The
    /// user's other sessions are untouched, except for the oldest one when the per-user cap is full.
    /// </summary>
    /// <param name="user">The authenticated user.</param>
    /// <param name="ipAddress">Optional client IP recorded on the new session.</param>
    /// <param name="userAgent">Optional client user-agent recorded on the new session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected Task<Result<AuthenticationResponse>> IssueTokensAsync(
        TUser user,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        return sessionIssuer.IssueAsync(
            user.Id,
            sessionId => CreateAccessTokenForSession(user, sessionId),
            ipAddress,
            userAgent,
            cancellationToken);
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

    /// <summary>
    /// Mints the access token for a specific refresh session, so the token can name the device it
    /// belongs to (the standard <c>sid</c> claim).
    /// </summary>
    /// <remarks>
    /// The default stamps the claim without the app hook participating: the issuer arms
    /// <see cref="TokenService"/> for the duration of the <see cref="CreateAccessToken"/> call
    /// (<see cref="IAuthSessionIssuer.MintForSession"/>), and the wrapper appends <c>sid</c>, plus
    /// <c>mfa</c> when this request verified a second factor, to whatever claim set the app passed.
    /// Doing it there rather than by changing <see cref="CreateAccessToken"/>'s signature is what makes
    /// the claims additive: every existing subclass keeps compiling and emits them with no edit.
    /// Override this method instead of relying on the wrapper if an app mints from its own
    /// token-service reference.
    /// </remarks>
    /// <param name="user">The authenticated user.</param>
    /// <param name="sessionId">The refresh session the token is being minted for.</param>
    /// <returns>The signed access token.</returns>
    protected virtual string CreateAccessTokenForSession(TUser user, Guid sessionId) =>
        sessionIssuer.MintForSession(sessionId, _multiFactorMethod, () => CreateAccessToken(user));

    /// <summary>
    /// The email-confirmation sign-in gate. Off unless the host both supplied
    /// <see cref="EmailConfirmationSettings"/> with <c>RequireConfirmedEmail</c> set AND the app's
    /// <c>User</c> implements <see cref="IEmailConfirmableUser"/>, so adopting the token workflow
    /// without flipping the flag changes nothing about who can sign in.
    /// </summary>
    /// <param name="untrackedUser">The candidate whose password has just been proved.</param>
    /// <returns>A success result, or the <c>Authentication.EmailNotConfirmed</c> failure.</returns>
    protected virtual Result CheckEmailConfirmed(TUser untrackedUser)
    {
        if (emailConfirmationSettings?.Value.RequireConfirmedEmail != true)
        {
            return Result.Success();
        }

        return untrackedUser is IEmailConfirmableUser { IsEmailConfirmed: false }
            ? Result.Failure(EmailConfirmationErrors.EmailNotConfirmed(nameof(LoginAsync)))
            : Result.Success();
    }

    /// <summary>
    /// Runs the second-factor challenge for a candidate whose password has just been proved.
    /// </summary>
    /// <remarks>
    /// With no <see cref="ITwoFactorAuthenticator"/> injected there is no challenge and no extra
    /// query: the method answers <see cref="TwoFactorOutcome.NotEnrolled"/> outright, which is what
    /// keeps the feature free for an app that has not adopted it.
    /// </remarks>
    /// <param name="userId">The candidate account.</param>
    /// <param name="code">The second-factor code the caller supplied, if any.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How the challenge was satisfied, or the two-factor failure.</returns>
    protected virtual Task<Result<TwoFactorOutcome>> ChallengeSecondFactorAsync(
        UserIdentifierType userId,
        string? code,
        CancellationToken cancellationToken) =>
        twoFactor is null
            ? Task.FromResult(Result.Success(TwoFactorOutcome.NotEnrolled))
            : twoFactor.ChallengeAsync(userId, code, cancellationToken);

    /// <summary>
    /// Runs the second-factor challenge and counts a wrong code against the account exactly like a
    /// wrong password, so the lockout at the top of <see cref="LoginAsync"/> throttles code guessing
    /// per account. A missing code (<see cref="TwoFactorErrors.TwoFactorRequiredCode"/>) is the
    /// ordinary first leg of the challenge and is not counted.
    /// </summary>
    /// <param name="userId">The account that passed the password check.</param>
    /// <param name="request">The login request (email for the counter, the presented code).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The challenge result, unchanged.</returns>
    private async Task<Result<TwoFactorOutcome>> ChallengeSecondFactorCountingFailuresAsync(
        UserIdentifierType userId,
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var secondFactor = await ChallengeSecondFactorAsync(userId, request.TwoFactorCode, cancellationToken)
            .ConfigureAwait(false);
        if (secondFactor.IsFailure && secondFactor.Errors.Any(e => e.Code == TwoFactorErrors.TwoFactorInvalidCode))
        {
            await loginProtection.IncrementFailedAttemptsAsync(request.Email, cancellationToken).ConfigureAwait(false);
        }

        return secondFactor;
    }

    /// <summary>
    /// Maps a challenge outcome onto the value the <c>mfa</c> claim carries, or null when nothing was
    /// challenged and the token should carry no claim at all.
    /// </summary>
    /// <param name="outcome">How the challenge was satisfied.</param>
    /// <returns>The claim value, or <see langword="null"/>.</returns>
    private static string? MultiFactorMethodFor(TwoFactorOutcome outcome) => outcome switch
    {
        TwoFactorOutcome.VerifiedTotp => AuthClaimTypes.MultiFactorMethodTotp,
        TwoFactorOutcome.VerifiedRecoveryCode => AuthClaimTypes.MultiFactorMethodRecoveryCode,
        TwoFactorOutcome.NotEnrolled => null,

        // An outcome this method has not been taught about must not silently mint an mfa claim: an
        // unknown value means the enum grew and this map did not.
        _ => null,
    };

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
    /// Whether the account carries stored password material at all. An external-OAuth account
    /// carries none (ADR-036), and password login is not a path such an account has.
    /// </summary>
    private static bool HasStoredCredential(TUser user) =>
        user.PasswordHash.Length > 0 && user.PasswordSalt.Length > 0;

    /// <summary>
    /// Runs one throwaway key derivation so a login branch that never reaches the real verification
    /// still pays its cost. Without it the 401 for an address with no usable credential comes back
    /// in a fraction of the time a real check takes, which is a membership oracle.
    /// </summary>
    /// <remarks>
    /// The derivation is a <see cref="IPasswordHasher.HashPassword"/> of the submitted password,
    /// discarded. Hashing and verifying each run the hasher's own key derivation once, so the cost
    /// matches a real verification for whichever hasher is registered. A verification against fixed
    /// decoy material would not: a hasher that rejects material of the wrong size before deriving
    /// anything (the shipped one does) answers it instantly once the decoy's sizes stop matching its
    /// own, which silently reopens the timing gap.
    /// </remarks>
    private void BurnPasswordVerificationCost(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        _ = passwordHasher.HashPassword(password);
    }

    /// <summary>
    /// The registration conflict, returned both by the up-front email check and by the
    /// unique-index race recovery in <see cref="RegisterAsync"/> so the two paths are
    /// indistinguishable to the caller.
    /// </summary>
    private static Result<AuthenticationResponse> EmailAlreadyExistsFailure() =>
        Result.Failure<AuthenticationResponse>(
            Error.Conflict(AuthErrorCodes.EmailAlreadyExists, "An account with this email already exists.", nameof(RegisterAsync)));
}
