using AwesomeAssertions;
using FluentValidation;
using MMCA.Common.Application.Auth;
using MMCA.Common.Shared.Auth;
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
