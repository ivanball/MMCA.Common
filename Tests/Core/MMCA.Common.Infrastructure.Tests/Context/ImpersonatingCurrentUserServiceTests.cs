using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Context;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Context;

/// <summary>
/// With no override set every member reads through to the registered service; with an override set
/// every member answers from the override principal, and claim values parse in the invariant culture.
/// </summary>
public sealed class ImpersonatingCurrentUserServiceTests
{
    private readonly Mock<ICurrentUserService> _inner = new(MockBehavior.Strict);
    private readonly ScopedUserOverride _override = new();

    [Fact]
    public void NoOverride_GetClaimValue_ReadsThroughToTheInnerService()
    {
        _inner.Setup(i => i.GetClaimValue<int>("speaker_id")).Returns(42);

        var value = CreateSut().GetClaimValue<int>("speaker_id");

        value.Should().Be(42);
        _inner.Verify(i => i.GetClaimValue<int>("speaker_id"), Times.Once);
    }

    [Fact]
    public void NoOverride_UserIdAndRoleAndUser_ReadThroughToTheInnerService()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity("Inner"));
        _inner.SetupGet(i => i.User).Returns(principal);
        _inner.SetupGet(i => i.UserId).Returns(7);
        _inner.SetupGet(i => i.Role).Returns("Admin");

        var sut = CreateSut();

        sut.User.Should().BeSameAs(principal);
        sut.UserId.Should().Be(7);
        sut.Role.Should().Be("Admin");
    }

    [Fact]
    public void Override_GetClaimValue_ParsesTheOverrideClaim_WithoutAskingTheInnerService()
    {
        _override.Set(Principal(new Claim("speaker_id", "99")));

        var value = CreateSut().GetClaimValue<int>("speaker_id");

        value.Should().Be(99);
        _inner.Verify(i => i.GetClaimValue<int>(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Override_GetClaimValue_ReturnsNull_WhenTheClaimIsAbsent()
    {
        _override.Set(Principal(new Claim("other", "1")));

        CreateSut().GetClaimValue<int>("speaker_id").Should().BeNull();
    }

    [Fact]
    public void Override_GetClaimValue_ReturnsNull_WhenTheClaimDoesNotParse()
    {
        _override.Set(Principal(new Claim("speaker_id", "not-a-number")));

        CreateSut().GetClaimValue<int>("speaker_id").Should().BeNull();
    }

    [Fact]
    public void Override_GetClaimValue_ParsesInTheInvariantCulture_WhateverTheAmbientCulture()
    {
        _override.Set(Principal(new Claim("ratio", "1.5")));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // de-DE reads "1.5" as fifteen (the dot is its group separator).
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            CreateSut().GetClaimValue<decimal>("ratio").Should().Be(1.5m);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Override_GetClaimValue_ParsesAGuid()
    {
        var id = Guid.NewGuid();
        _override.Set(Principal(new Claim("session_id", id.ToString())));

        CreateSut().GetClaimValue<Guid>("session_id").Should().Be(id);
    }

    [Fact]
    public void Override_UserAndRole_AnswerFromTheOverridePrincipal()
    {
        var principal = Principal(new Claim(ClaimTypes.Role, "Support"));
        _override.Set(principal);

        var sut = CreateSut();

        sut.User.Should().BeSameAs(principal);
        sut.Role.Should().Be("Support");
    }

    [Fact]
    public void ClearedOverride_ReadsThroughAgain()
    {
        _override.Set(Principal(new Claim("speaker_id", "99")));
        _override.Clear();
        _inner.Setup(i => i.GetClaimValue<int>("speaker_id")).Returns(5);

        CreateSut().GetClaimValue<int>("speaker_id").Should().Be(5);
    }

    private ImpersonatingCurrentUserService CreateSut() => new(_inner.Object, _override);

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Override"));
}
