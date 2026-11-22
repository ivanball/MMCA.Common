using FluentValidation;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Legal;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Parameter object bundling the FluentValidation validators for the authentication workflows.
/// Collapsing three closely-related dependencies into one keeps the app's
/// <c>AuthenticationService</c> below the application-service constructor-arity ceiling
/// (a god-class guardrail) without sacrificing per-request validation. The request DTOs already
/// live in <c>MMCA.Common.Shared.Auth</c>, so the bundle is app-agnostic.
/// </summary>
/// <param name="login">Validator for <see cref="LoginRequest"/>.</param>
/// <param name="register">Validator for <see cref="RegisterRequest"/>.</param>
/// <param name="refresh">Validator for <see cref="RefreshTokenRequest"/>.</param>
/// <param name="legalAcceptance">
/// Optional Terms of Service options (bound by <c>AddLegalAcceptance(configuration)</c>), carried
/// here because the terms check is a registration rule: bundling it keeps the opt-in from adding a
/// constructor dependency to <c>AuthenticationServiceBase</c> or to the app's subclass. Null, or no
/// configured version, means registration does not ask for acceptance.
/// </param>
[method: System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0027:API with optional parameter(s) should have the most parameters amongst its public overloads", Justification = "The shipped constructor keeps its optional legalAcceptance parameter (removing the default breaks every three-argument call). The longer overload has no optional parameter, so positional calls with three or four arguments bind to this constructor unambiguously, and the container picks the longer one only when an ITwoFactorAuthenticator is registered.")]
public sealed class AuthenticationValidators(
    IValidator<LoginRequest> login,
    IValidator<RegisterRequest> register,
    IValidator<RefreshTokenRequest> refresh,
    IOptions<LegalAcceptanceOptions>? legalAcceptance = null)
{
    /// <summary>
    /// Initializes the bundle with the sign-in second-factor challenge as well. The container picks
    /// this constructor only when an <see cref="ITwoFactorAuthenticator"/> is registered (by
    /// <c>AddTwoFactorAuthentication(configuration)</c>), so a host that has not adopted two-factor
    /// authentication resolves the primary constructor and is unchanged. Carrying the authenticator
    /// here is what lets an app adopt the challenge without a new constructor argument on its
    /// <c>AuthenticationService</c>.
    /// </summary>
    /// <param name="login">Validator for <see cref="LoginRequest"/>.</param>
    /// <param name="register">Validator for <see cref="RegisterRequest"/>.</param>
    /// <param name="refresh">Validator for <see cref="RefreshTokenRequest"/>.</param>
    /// <param name="legalAcceptance">Optional Terms of Service options, as on the primary constructor.</param>
    /// <param name="twoFactor">The second-factor challenge the sign-in flow runs after the password check.</param>
    public AuthenticationValidators(
        IValidator<LoginRequest> login,
        IValidator<RegisterRequest> register,
        IValidator<RefreshTokenRequest> refresh,
        IOptions<LegalAcceptanceOptions>? legalAcceptance,
        ITwoFactorAuthenticator twoFactor)
        : this(login, register, refresh, legalAcceptance) => TwoFactor = twoFactor;

    /// <summary>Gets the login request validator.</summary>
    public IValidator<LoginRequest> Login { get; } = login;

    /// <summary>Gets the registration request validator.</summary>
    public IValidator<RegisterRequest> Register { get; } = register;

    /// <summary>Gets the refresh-token request validator.</summary>
    public IValidator<RefreshTokenRequest> Refresh { get; } = refresh;

    /// <summary>
    /// Gets the Terms of Service version registration requires, or <see langword="null"/> when terms
    /// acceptance is not configured (see <see cref="LegalAcceptancePolicy.ResolveCurrentVersion"/>).
    /// </summary>
    public string? CurrentTermsVersion => LegalAcceptancePolicy.ResolveCurrentVersion(legalAcceptance?.Value);

    /// <summary>
    /// Gets the sign-in second-factor challenge, or <see langword="null"/> when the host registered
    /// none. <c>AuthenticationServiceBase</c> uses it when its own <c>twoFactor</c> constructor
    /// argument is null; an explicit constructor argument wins.
    /// </summary>
    public ITwoFactorAuthenticator? TwoFactor { get; }
}
