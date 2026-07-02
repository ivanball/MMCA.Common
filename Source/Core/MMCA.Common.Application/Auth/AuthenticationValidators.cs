using FluentValidation;
using MMCA.Common.Shared.Auth;

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
public sealed class AuthenticationValidators(
    IValidator<LoginRequest> login,
    IValidator<RegisterRequest> register,
    IValidator<RefreshTokenRequest> refresh)
{
    /// <summary>Gets the login request validator.</summary>
    public IValidator<LoginRequest> Login { get; } = login;

    /// <summary>Gets the registration request validator.</summary>
    public IValidator<RegisterRequest> Register { get; } = register;

    /// <summary>Gets the refresh-token request validator.</summary>
    public IValidator<RefreshTokenRequest> Refresh { get; } = refresh;
}
