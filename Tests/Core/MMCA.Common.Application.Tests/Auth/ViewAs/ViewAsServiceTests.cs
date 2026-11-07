using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Testing.Support;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.ViewAs;

/// <summary>
/// <see cref="ViewAsService"/> (T20, ADR-137): who may be viewed, what the grant row records, and
/// that ending a grant ends it.
/// </summary>
public sealed class ViewAsServiceTests
{
    private const UserIdentifierType StaffUserId = 1;
    private const UserIdentifierType TargetUserId = 2;
    private const string Reason = "Ticket 4411: the user cannot see their schedule";

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly InMemoryViewAsGrantStore _grants = new();
    private readonly RecordingTokenService _tokens = new();
    private readonly Mock<IViewAsSubjectResolver> _subjects = new();

    private readonly IPermissionRegistry _permissions = new PermissionRegistryBuilder()
        .Grant("Organizer", AdministrationPermissions.ManageUsers, AdministrationPermissions.ViewAsUsers)
        .Grant("Attendee", "sessions:read")
        .Build();

    public ViewAsServiceTests() =>
        _subjects.Setup(s => s.FindAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ViewAsSubject(TargetUserId, "target@example.com", "Attendee", "Target User", IsDeleted: false));

    // Spec B assertion 3 (self): the caller can never view their own account.
    [Fact]
    public async Task StartAsync_RefusesTheCallersOwnAccount()
    {
        var result = await CreateSut().StartAsync(StaffUserId, StaffUserId, Reason);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("ViewAs.Self");
        _grants.Grants.Should().BeEmpty();
        _tokens.Minted.Should().BeEmpty();
    }

    // Spec B assertion 3 (deleted): a soft-deleted account is never viewed.
    [Fact]
    public async Task StartAsync_RefusesASoftDeletedAccount()
    {
        _subjects.Setup(s => s.FindAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ViewAsSubject(TargetUserId, "gone@example.com", "Attendee", "Gone User", IsDeleted: true));

        var result = await CreateSut().StartAsync(StaffUserId, TargetUserId, Reason);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("ViewAs.TargetDeleted");
        _grants.Grants.Should().BeEmpty();
        _tokens.Minted.Should().BeEmpty();
    }

    // Spec B assertion 3 (no lateral access): an account that itself holds users:view-as is refused.
    [Fact]
    public async Task StartAsync_RefusesAnAccountThatItselfHoldsViewAs()
    {
        _subjects.Setup(s => s.FindAsync(TargetUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ViewAsSubject(TargetUserId, "staff2@example.com", "Organizer", "Other Staff", IsDeleted: false));

        var result = await CreateSut().StartAsync(StaffUserId, TargetUserId, Reason);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be("ViewAs.TargetIsStaff");
        _grants.Grants.Should().BeEmpty();
        _tokens.Minted.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_RefusesAnUnknownAccountAsNotFound()
    {
        var result = await CreateSut().StartAsync(StaffUserId, 99, Reason);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("too short")]
    public async Task StartAsync_RequiresAReasonOfAtLeastTenCharacters(string? reason)
    {
        var result = await CreateSut().StartAsync(StaffUserId, TargetUserId, reason);

        result.Errors.Should().ContainSingle().Which.Code.Should().Be("ViewAs.ReasonInvalid");
        _grants.Grants.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_StoresTheGrantRow_AndMintsTheTargetsClaimsPlusActSubAndVag()
    {
        var result = await CreateSut().StartAsync(StaffUserId, TargetUserId, Reason);

        var response = result.Value!;
        var grant = _grants.Grants.Should().ContainSingle().Subject;
        grant.StaffUserId.Should().Be(StaffUserId);
        grant.TargetUserId.Should().Be(TargetUserId);
        grant.Reason.Should().Be(Reason);
        grant.GrantedAt.Should().Be(Now.UtcDateTime);
        grant.ExpiresAt.Should().Be(Now.UtcDateTime.AddMinutes(15));
        response.GrantId.Should().Be(grant.Id);
        response.AccessTokenExpiry.Should().Be(grant.ExpiresAt);

        var minted = _tokens.Minted.Should().ContainSingle().Subject;
        minted.UserId.Should().Be(TargetUserId);
        minted.Role.Should().Be("Attendee");
        minted.Lifetime.Should().Be(TimeSpan.FromMinutes(15));
        minted.Claims.Should().Contain(c => c.Type == AuthClaimTypes.ViewAsActor && c.Value == "1");
        minted.Claims.Should().Contain(c => c.Type == AuthClaimTypes.ViewAsGrant && c.Value == grant.Id.ToString());
    }

    [Fact]
    public async Task StartAsync_ShortensTheGrantToAShorterConfiguredAccessTokenLifetime()
    {
        _tokens.Lifetime = TimeSpan.FromMinutes(5);

        var result = await CreateSut().StartAsync(StaffUserId, TargetUserId, Reason);

        result.Value!.AccessTokenExpiry.Should().Be(Now.UtcDateTime.AddMinutes(5));
        _tokens.Minted.Single().Lifetime.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task EndAsync_EndsTheGrant_SoItIsNoLongerActive()
    {
        var sut = CreateSut();
        var grantId = (await sut.StartAsync(StaffUserId, TargetUserId, Reason)).Value!.GrantId;
        (await sut.IsGrantActiveAsync(grantId, StaffUserId, TargetUserId)).Should().BeTrue();

        var ended = await sut.EndAsync(grantId, StaffUserId);

        ended.IsSuccess.Should().BeTrue();
        (await sut.IsGrantActiveAsync(grantId, StaffUserId, TargetUserId)).Should().BeFalse();
        _grants.Grants.Single().EndedAt.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task IsGrantActiveAsync_IsFalseOnceTheGrantExpires_AndForAMismatchedParty()
    {
        var sut = CreateSut();
        var grantId = (await sut.StartAsync(StaffUserId, TargetUserId, Reason)).Value!.GrantId;

        (await sut.IsGrantActiveAsync(grantId, StaffUserId, 3)).Should().BeFalse("the token names another account");
        (await sut.IsGrantActiveAsync(grantId, 3, TargetUserId)).Should().BeFalse("the token names another staff user");

        _clock.Advance(TimeSpan.FromMinutes(15));
        (await sut.IsGrantActiveAsync(grantId, StaffUserId, TargetUserId)).Should().BeFalse("the grant expired");
    }

    [Fact]
    public async Task EndAsync_AnotherStaffUsersGrant_IsNotFound()
    {
        var sut = CreateSut();
        var grantId = (await sut.StartAsync(StaffUserId, TargetUserId, Reason)).Value!.GrantId;

        var ended = await sut.EndAsync(grantId, 3);

        ended.Errors.Should().ContainSingle().Which.Type.Should().Be(ErrorType.NotFound);
        _grants.Grants.Single().EndedAt.Should().BeNull();
    }

    private ViewAsService CreateSut() => new(
        _grants,
        _tokens,
        _permissions,
        Options.Create(new ViewAsSettings { Enabled = true }),
        _clock,
        _subjects.Object);

    /// <summary>Records each mint instead of signing, so the claim set and lifetime are inspectable.</summary>
    private sealed class RecordingTokenService : ITokenService
    {
        public List<(UserIdentifierType UserId, string Role, IReadOnlyList<Claim> Claims, TimeSpan? Lifetime)> Minted { get; } = [];

        public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(60);

        public TimeSpan AccessTokenLifetime => Lifetime;

        public string GenerateAccessToken(UserIdentifierType userId, string email, string role, string fullName, IEnumerable<Claim>? additionalClaims = null)
        {
            Minted.Add((userId, role, [.. additionalClaims ?? []], null));
            return "token-" + Minted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public string GenerateShortLivedAccessToken(UserIdentifierType userId, string email, string role, string fullName, IEnumerable<Claim>? additionalClaims, TimeSpan lifetime)
        {
            Minted.Add((userId, role, [.. additionalClaims ?? []], lifetime));
            return "token-" + Minted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public string GenerateRefreshToken() => throw new InvalidOperationException("A view-as grant never mints a refresh token.");

        public ClaimsPrincipal? GetPrincipalFromExpiredToken(string token) => null;
    }
}
