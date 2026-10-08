using AwesomeAssertions;
using MMCA.Common.API.Authorization;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.API.Tests.Authorization;

/// <summary>
/// The two fail-closed ownership gates hosts used to hand-write per controller (Store
/// <c>OrdersController</c>, <c>ShoppingCartsController</c>, <c>ReviewsController</c>; ADC
/// <c>SessionQuestionAnswersController</c>, <c>EventQuestionAnswersController</c>). Ported from the
/// Store controller tests that pinned them: a caller holding the bypass role passes untouched, a
/// non-privileged caller whose owner claim cannot be resolved is refused with 403 rather than
/// treated as an unscoped reader, and a resolvable caller who does not own the row gets 404 so the
/// row's existence is not disclosed.
/// </summary>
public sealed class OwnershipHelperGateTests
{
    private const string BypassRole = "Admin";
    private const string ClaimType = "customer_id";
    private const string Source = "OrdersController";
    private const string Target = "Order";

    private readonly Mock<ICurrentUserService> _currentUserService = new() { CallBase = true };

    // -- RequireResolvableOwner --
    [Fact]
    public void RequireResolvableOwner_BypassRole_Succeeds()
    {
        _currentUserService.Setup(s => s.Role).Returns("Admin");
        _currentUserService.Setup(s => s.GetClaimValue<int>(ClaimType)).Returns((int?)null);

        Result result = OwnershipHelper.RequireResolvableOwner<int>(
            _currentUserService.Object, ClaimType, BypassRole, Source, Target);

        result.IsSuccess.Should().BeTrue("the bypass role may read unscoped even with no owner claim");
    }

    [Fact]
    public void RequireResolvableOwner_NonPrivilegedWithResolvableClaim_Succeeds()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>(ClaimType)).Returns(42);

        Result result = OwnershipHelper.RequireResolvableOwner<int>(
            _currentUserService.Object, ClaimType, BypassRole, Source, Target);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void RequireResolvableOwner_NonPrivilegedWithoutClaim_FailsForbiddenWithTheCallersSourceAndTarget()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>(ClaimType)).Returns((int?)null);

        Result result = OwnershipHelper.RequireResolvableOwner<int>(
            _currentUserService.Object, ClaimType, BypassRole, Source, Target);

        result.IsFailure.Should().BeTrue("an unresolvable owner must never read unscoped");
        result.Errors.Should().ContainSingle().Which.Should().Be(
            Error.Forbidden("Error.Forbidden", "Access denied.", Source, Target));
    }

    [Fact]
    public void RequireResolvableOwner_IsGenericOverTheClaimValueType()
    {
        var ownerId = Guid.NewGuid();
        _currentUserService.Setup(s => s.Role).Returns("Attendee");
        _currentUserService.Setup(s => s.GetClaimValue<Guid>("sub")).Returns(ownerId);

        Result result = OwnershipHelper.RequireResolvableOwner<Guid>(
            _currentUserService.Object, "sub", "Organizer", Source, Target);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void RequireResolvableOwner_NullService_Throws()
    {
        Action act = () => OwnershipHelper.RequireResolvableOwner<int>(null!, ClaimType, BypassRole, Source, Target);

        act.Should().Throw<ArgumentNullException>();
    }

    // -- ValidateOwnershipAsync --
    [Fact]
    public async Task ValidateOwnershipAsync_BypassRole_SucceedsWithoutQueryingOwnership()
    {
        _currentUserService.Setup(s => s.Role).Returns("Admin");
        var queried = false;

        Result result = await OwnershipHelper.ValidateOwnershipAsync<int>(
            _currentUserService.Object,
            ClaimType,
            BypassRole,
            (_, _) =>
            {
                queried = true;
                return Task.FromResult(false);
            },
            Source,
            Target,
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        queried.Should().BeFalse("the bypass role skips the ownership lookup entirely");
    }

    [Fact]
    public async Task ValidateOwnershipAsync_NonPrivilegedWithoutClaim_FailsForbiddenWithoutQuerying()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>(ClaimType)).Returns((int?)null);
        var queried = false;

        Result result = await OwnershipHelper.ValidateOwnershipAsync<int>(
            _currentUserService.Object,
            ClaimType,
            BypassRole,
            (_, _) =>
            {
                queried = true;
                return Task.FromResult(true);
            },
            Source,
            Target,
            TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Should().Be(
            Error.Forbidden("Error.Forbidden", "Access denied.", Source, Target));
        queried.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateOwnershipAsync_Owner_SucceedsAndPassesTheResolvedOwnerToTheLookup()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>(ClaimType)).Returns(42);
        int? seenOwner = null;
        using var cts = new CancellationTokenSource();
        CancellationToken seenToken = default;

        Result result = await OwnershipHelper.ValidateOwnershipAsync<int>(
            _currentUserService.Object,
            ClaimType,
            BypassRole,
            (ownerId, token) =>
            {
                seenOwner = ownerId;
                seenToken = token;
                return Task.FromResult(true);
            },
            Source,
            Target,
            cts.Token);

        result.IsSuccess.Should().BeTrue();
        seenOwner.Should().Be(42);
        seenToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task ValidateOwnershipAsync_NotOwner_FailsNotFoundSoTheRowsExistenceIsNotDisclosed()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>(ClaimType)).Returns(42);

        Result result = await OwnershipHelper.ValidateOwnershipAsync<int>(
            _currentUserService.Object,
            ClaimType,
            BypassRole,
            (_, _) => Task.FromResult(false),
            Source,
            Target,
            TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle().Which.Should().Be(
            Error.NotFound.WithSource(Source).WithTarget(Target));
    }

    [Fact]
    public async Task ValidateOwnershipAsync_NullLookup_Throws()
    {
        var act = () => OwnershipHelper.ValidateOwnershipAsync<int>(
            _currentUserService.Object, ClaimType, BypassRole, null!, Source, Target, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
