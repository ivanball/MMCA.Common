using FluentValidation;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.Legal;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Parameter object bundling the FluentValidation validators for the authentication workflows.
/// Collapsing three closely-related dependencies into one keeps the app's
/// <c>AuthenticationService</c> below the application-service constructor-arity ceiling
/// (a god-class guardrail) without sacrificing per-request validation. The request DTOs already
/// live in <c>MMCA.Common.Shared.Auth</c>, so the bundle is app-agnostic (hoisted from the apps).
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
public sealed class AuthenticationValidators(
    IValidator<LoginRequest> login,
    IValidator<RegisterRequest> register,
    IValidator<RefreshTokenRequest> refresh,
    IOptions<LegalAcceptanceOptions>? legalAcceptance = null)
{
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
}
