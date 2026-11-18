using System.Linq.Expressions;
using System.Security.Claims;
using AwesomeAssertions;
using MMCA.Common.API.Authorization;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.API.Tests.Authorization;

public sealed class OwnershipHelperTests
{
    // The framework declares no role names, so the bypass role is the suite's own.
    private const string BypassRole = "Admin";
    private const string ClaimType = "customer_id";
    private const string Source = "OrdersController";
    private const string Target = "Order";

    private readonly Mock<ICurrentUserService> _currentUserService = new() { CallBase = true };

    // ── Test specification ──
    private sealed class TestOwnerSpecification(int customerId) : Specification<AuditableBaseEntity<int>, int>
    {
        public int CustomerId { get; } = customerId;

        public override Expression<Func<AuditableBaseEntity<int>, bool>> Criteria =>
            e => true;
    }

    // ── IsAdmin ──
    [Fact]
    public void IsAdmin_WhenRoleIsAdmin_ReturnsTrue()
    {
        _currentUserService.Setup(s => s.Role).Returns("Admin");

        bool result = OwnershipHelper.IsAdmin(_currentUserService.Object, BypassRole);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAdmin_WhenRoleIsAdminCaseInsensitive_ReturnsTrue()
    {
        _currentUserService.Setup(s => s.Role).Returns("admin");

        bool result = OwnershipHelper.IsAdmin(_currentUserService.Object, BypassRole);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAdmin_WhenRoleIsMember_ReturnsFalse()
    {
        _currentUserService.Setup(s => s.Role).Returns("Member");

        bool result = OwnershipHelper.IsAdmin(_currentUserService.Object, BypassRole);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsAdmin_WhenRoleIsNull_ReturnsFalse()
    {
        _currentUserService.Setup(s => s.Role).Returns((string?)null);

        bool result = OwnershipHelper.IsAdmin(_currentUserService.Object, BypassRole);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsAdmin_WhenTheBypassRoleIsNotTheFirstRoleClaim_ReturnsTrue()
    {
        // Role is the FIRST role claim only; the bypass role arriving second must still count, or
        // the helper disagrees with an inline IsInRole check on the same caller.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "Member"), new Claim(ClaimTypes.Role, "Admin")],
            "Test"));
        _currentUserService.Setup(s => s.User).Returns(principal);
        _currentUserService.Setup(s => s.Role).Returns("Member");

        bool result = OwnershipHelper.IsAdmin(_currentUserService.Object, BypassRole);

        result.Should().BeTrue();
        _currentUserService.Object.IsInRole(BypassRole).Should().Be(result, "the helper and IsInRole must agree on who is privileged");
    }

    [Fact]
    public void IsAdmin_NullService_ThrowsArgumentNullException()
    {
        Action act = () => OwnershipHelper.IsAdmin(null!, BypassRole);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── GetOwnershipSpecification (generic) ──
    [Fact]
    public void GetOwnershipSpecification_AdminUser_ReturnsNull()
    {
        _currentUserService.Setup(s => s.Role).Returns("Admin");

        var result = OwnershipHelper.GetOwnershipSpecification<TestOwnerSpecification, int>(
            _currentUserService.Object,
            "customer_id",
            id => new TestOwnerSpecification(id),
            BypassRole);

        result.Should().BeNull();
    }

    [Fact]
    public void GetOwnershipSpecification_NonAdminWithClaim_ReturnsSpecification()
    {
        _currentUserService.Setup(s => s.Role).Returns("Member");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns(42);

        var result = OwnershipHelper.GetOwnershipSpecification<TestOwnerSpecification, int>(
            _currentUserService.Object,
            "customer_id",
            id => new TestOwnerSpecification(id),
            BypassRole);

        result.Should().NotBeNull();
        result!.CustomerId.Should().Be(42);
    }

    [Fact]
    public void GetOwnershipSpecification_NonAdminWithoutClaim_ReturnsNull()
    {
        _currentUserService.Setup(s => s.Role).Returns("Member");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns((int?)null);

        var result = OwnershipHelper.GetOwnershipSpecification<TestOwnerSpecification, int>(
            _currentUserService.Object,
            "customer_id",
            id => new TestOwnerSpecification(id),
            BypassRole);

        result.Should().BeNull();
    }

    [Fact]
    public void GetOwnershipSpecification_NullService_ThrowsArgumentNullException()
    {
        Action act = () => OwnershipHelper.GetOwnershipSpecification<TestOwnerSpecification, int>(
            null!,
            "customer_id",
            id => new TestOwnerSpecification(id),
            BypassRole);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetOwnershipSpecification_NullFactory_ThrowsArgumentNullException()
    {
        Action act = () => OwnershipHelper.GetOwnershipSpecification<TestOwnerSpecification, int>(
            _currentUserService.Object,
            "customer_id",
            null!,
            BypassRole);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── GetOwnershipSpecification (convenience overload with default customer_id) ──
    [Fact]
    public void GetOwnershipSpecification_ConvenienceOverload_AdminReturnsNull()
    {
        _currentUserService.Setup(s => s.Role).Returns("Admin");

        var result = OwnershipHelper.GetOwnershipSpecification(
            _currentUserService.Object,
            id => new TestOwnerSpecification(id),
            BypassRole);

        result.Should().BeNull();
    }

    [Fact]
    public void GetOwnershipSpecification_ConvenienceOverload_NonAdminWithClaim_ReturnsSpecification()
    {
        _currentUserService.Setup(s => s.Role).Returns("Member");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns(99);

        var result = OwnershipHelper.GetOwnershipSpecification(
            _currentUserService.Object,
            id => new TestOwnerSpecification(id),
            BypassRole);

        result.Should().NotBeNull();
        result!.CustomerId.Should().Be(99);
    }

    // ── Fail-closed ownership gates ──
    // The two gates hosts used to hand-write per controller (Store OrdersController,
    // ShoppingCartsController, ReviewsController; ADC SessionQuestionAnswersController,
    // EventQuestionAnswersController). Ported from the Store controller tests that pinned them: a
    // caller holding the bypass role passes untouched, a non-privileged caller whose owner claim
    // cannot be resolved is refused with 403 rather than treated as an unscoped reader, and a
    // resolvable caller who does not own the row gets 404 so the row's existence is not disclosed.
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
