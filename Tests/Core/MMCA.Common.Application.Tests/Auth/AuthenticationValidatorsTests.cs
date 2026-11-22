using AwesomeAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Legal;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Shared.Auth.Requests;
using Moq;

namespace MMCA.Common.Application.Tests.Auth;

/// <summary>
/// Verifies the <see cref="AuthenticationValidators"/> parameter object exposes each
/// constructor-supplied validator unchanged (the individual request validators have their
/// own dedicated test classes under <c>Auth/Validation</c>).
/// </summary>
public sealed class AuthenticationValidatorsTests
{
    [Fact]
    public void Login_ReturnsValidatorPassedToConstructor()
    {
        var (sut, login, _, _) = CreateSut();

        sut.Login.Should().BeSameAs(login);
    }

    [Fact]
    public void Register_ReturnsValidatorPassedToConstructor()
    {
        var (sut, _, register, _) = CreateSut();

        sut.Register.Should().BeSameAs(register);
    }

    [Fact]
    public void Refresh_ReturnsValidatorPassedToConstructor()
    {
        var (sut, _, _, refresh) = CreateSut();

        sut.Refresh.Should().BeSameAs(refresh);
    }

    // ── The sign-in authenticator reaches the service through the bundle (no constructor argument) ──
    [Fact]
    public void ResolvedByTheContainer_WithAnAuthenticatorRegistered_CarriesThatAuthenticator()
    {
        var authenticator = new Mock<ITwoFactorAuthenticator>().Object;
        var services = ValidatorServices();
        services.AddSingleton(authenticator);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<AuthenticationValidators>().TwoFactor.Should().BeSameAs(
            authenticator,
            "a host that called AddTwoFactorAuthentication arms the challenge without changing its service constructor");
    }

    [Fact]
    public void ResolvedByTheContainer_WithNoAuthenticatorRegistered_CarriesNone()
    {
        using var provider = ValidatorServices().BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<AuthenticationValidators>().TwoFactor.Should().BeNull(
            "a host that adopted nothing resolves the bundle through the original constructor");
    }

    [Fact]
    public void ConstructedPositionallyWithThreeOrFourArguments_BindsTheOriginalConstructor_AndCarriesNoAuthenticator()
    {
        var (three, _, _, _) = CreateSut();
        var four = new AuthenticationValidators(
            new Mock<IValidator<LoginRequest>>().Object,
            new Mock<IValidator<RegisterRequest>>().Object,
            new Mock<IValidator<RefreshTokenRequest>>().Object,
            Options.Create(new LegalAcceptanceOptions()));

        three.TwoFactor.Should().BeNull();
        four.TwoFactor.Should().BeNull();
    }

    [Fact]
    public void ConstructedWithAnAuthenticator_CarriesIt()
    {
        var authenticator = new Mock<ITwoFactorAuthenticator>().Object;

        var sut = new AuthenticationValidators(
            new Mock<IValidator<LoginRequest>>().Object,
            new Mock<IValidator<RegisterRequest>>().Object,
            new Mock<IValidator<RefreshTokenRequest>>().Object,
            legalAcceptance: null,
            authenticator);

        sut.TwoFactor.Should().BeSameAs(authenticator);
    }

    private static ServiceCollection ValidatorServices()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(new Mock<IValidator<LoginRequest>>().Object);
        services.AddSingleton(new Mock<IValidator<RegisterRequest>>().Object);
        services.AddSingleton(new Mock<IValidator<RefreshTokenRequest>>().Object);
        services.AddScoped<AuthenticationValidators>();
        return services;
    }

    // ── Helpers ──
    private static (
        AuthenticationValidators Sut,
        IValidator<LoginRequest> Login,
        IValidator<RegisterRequest> Register,
        IValidator<RefreshTokenRequest> Refresh) CreateSut()
    {
        IValidator<LoginRequest> login = new Mock<IValidator<LoginRequest>>().Object;
        IValidator<RegisterRequest> register = new Mock<IValidator<RegisterRequest>>().Object;
        IValidator<RefreshTokenRequest> refresh = new Mock<IValidator<RefreshTokenRequest>>().Object;

        return (new AuthenticationValidators(login, register, refresh), login, register, refresh);
    }
}
