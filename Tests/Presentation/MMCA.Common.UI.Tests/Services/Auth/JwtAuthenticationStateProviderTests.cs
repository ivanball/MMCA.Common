#pragma warning disable VSTHRD002 // Synchronous wait in event handler — completed task in tests

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.UI.Services.Auth;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auth;

public class JwtAuthenticationStateProviderTests
{
    private readonly Mock<ITokenStorageService> _tokenStorage = new();

    private JwtAuthenticationStateProvider CreateSut() => new(_tokenStorage.Object);

    // ── GetAuthenticationStateAsync ──
    [Fact]
    public async Task GetAuthenticationStateAsync_WithNoToken_ReturnsAnonymous()
    {
        _tokenStorage.Setup(t => t.GetAccessTokenAsync()).ReturnsAsync((string?)null);

        var sut = CreateSut();
        var state = await sut.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task GetAuthenticationStateAsync_WithEmptyToken_ReturnsAnonymous()
    {
        _tokenStorage.Setup(t => t.GetAccessTokenAsync()).ReturnsAsync(string.Empty);

        var sut = CreateSut();
        var state = await sut.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task GetAuthenticationStateAsync_WithInvalidToken_ReturnsAnonymous()
    {
        _tokenStorage.Setup(t => t.GetAccessTokenAsync()).ReturnsAsync("not-a-jwt");

        var sut = CreateSut();
        var state = await sut.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task GetAuthenticationStateAsync_WithExpiredToken_ReturnsAnonymous()
    {
        var expiredToken = GenerateToken(DateTime.UtcNow.AddHours(-1));
        _tokenStorage.Setup(t => t.GetAccessTokenAsync()).ReturnsAsync(expiredToken);

        var sut = CreateSut();
        var state = await sut.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task GetAuthenticationStateAsync_WithValidToken_ReturnsAuthenticated()
    {
        var token = GenerateToken(DateTime.UtcNow.AddHours(1));
        _tokenStorage.Setup(t => t.GetAccessTokenAsync()).ReturnsAsync(token);

        var sut = CreateSut();
        var state = await sut.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task GetAuthenticationStateAsync_WithValidToken_ContainsClaims()
    {
        var token = GenerateToken(DateTime.UtcNow.AddHours(1), "test@example.com", "Admin");
        _tokenStorage.Setup(t => t.GetAccessTokenAsync()).ReturnsAsync(token);

        var sut = CreateSut();
        var state = await sut.GetAuthenticationStateAsync();

        state.User.FindFirst(ClaimTypes.Email)?.Value.Should().Be("test@example.com");
        state.User.FindFirst(ClaimTypes.Role)?.Value.Should().Be("Admin");
    }

    [Fact]
    public async Task GetAuthenticationStateAsync_WhenStorageThrows_ReturnsAnonymous()
    {
        _tokenStorage.Setup(t => t.GetAccessTokenAsync()).ThrowsAsync(new InvalidOperationException("JS interop"));

        var sut = CreateSut();
        var state = await sut.GetAuthenticationStateAsync();

        state.User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    // ── NotifyUserAuthentication ──
    [Fact]
    public void NotifyUserAuthentication_RaisesAuthenticationStateChanged()
    {
        var sut = CreateSut();
        var token = GenerateToken(DateTime.UtcNow.AddHours(1), "user@test.com");

        AuthenticationState? receivedState = null;
        sut.AuthenticationStateChanged += task => receivedState = task.GetAwaiter().GetResult();

        sut.NotifyUserAuthentication(token);

        receivedState.Should().NotBeNull();
        receivedState!.User.Identity!.IsAuthenticated.Should().BeTrue();
    }

    // ── NotifyUserLogout ──
    [Fact]
    public void NotifyUserLogout_RaisesAnonymousState()
    {
        var sut = CreateSut();

        AuthenticationState? receivedState = null;
        sut.AuthenticationStateChanged += task => receivedState = task.GetAwaiter().GetResult();

        sut.NotifyUserLogout();

        receivedState.Should().NotBeNull();
        receivedState!.User.Identity!.IsAuthenticated.Should().BeFalse();
    }

    // ── Helpers ──
    private static string GenerateToken(
        DateTime expires,
        string? email = null,
        string? role = null)
    {
        var key = new SymmetricSecurityKey(new byte[32]);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim> { new("sub", "1") };
        if (email is not null)
            claims.Add(new Claim(ClaimTypes.Email, email));
        if (role is not null)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var token = new JwtSecurityToken(
            issuer: "test",
            audience: "test",
            claims: claims,
            expires: expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
