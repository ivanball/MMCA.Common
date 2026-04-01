using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.API.Authorization;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;
using Moq;

namespace MMCA.Common.API.Tests.Authorization;

public sealed class OwnershipHelperTests
{
    private readonly Mock<ICurrentUserService> _currentUserService = new();

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

        bool result = OwnershipHelper.IsAdmin(_currentUserService.Object);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAdmin_WhenRoleIsAdminCaseInsensitive_ReturnsTrue()
    {
        _currentUserService.Setup(s => s.Role).Returns("admin");

        bool result = OwnershipHelper.IsAdmin(_currentUserService.Object);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsAdmin_WhenRoleIsCustomer_ReturnsFalse()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");

        bool result = OwnershipHelper.IsAdmin(_currentUserService.Object);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsAdmin_WhenRoleIsNull_ReturnsFalse()
    {
        _currentUserService.Setup(s => s.Role).Returns((string?)null);

        bool result = OwnershipHelper.IsAdmin(_currentUserService.Object);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsAdmin_NullService_ThrowsArgumentNullException()
    {
        Action act = () => OwnershipHelper.IsAdmin(null!);

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
            id => new TestOwnerSpecification(id));

        result.Should().BeNull();
    }

    [Fact]
    public void GetOwnershipSpecification_NonAdminWithClaim_ReturnsSpecification()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns(42);

        var result = OwnershipHelper.GetOwnershipSpecification<TestOwnerSpecification, int>(
            _currentUserService.Object,
            "customer_id",
            id => new TestOwnerSpecification(id));

        result.Should().NotBeNull();
        result!.CustomerId.Should().Be(42);
    }

    [Fact]
    public void GetOwnershipSpecification_NonAdminWithoutClaim_ReturnsNull()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns((int?)null);

        var result = OwnershipHelper.GetOwnershipSpecification<TestOwnerSpecification, int>(
            _currentUserService.Object,
            "customer_id",
            id => new TestOwnerSpecification(id));

        result.Should().BeNull();
    }

    [Fact]
    public void GetOwnershipSpecification_NullService_ThrowsArgumentNullException()
    {
        Action act = () => OwnershipHelper.GetOwnershipSpecification<TestOwnerSpecification, int>(
            null!,
            "customer_id",
            id => new TestOwnerSpecification(id));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GetOwnershipSpecification_NullFactory_ThrowsArgumentNullException()
    {
        Action act = () => OwnershipHelper.GetOwnershipSpecification<TestOwnerSpecification, int>(
            _currentUserService.Object,
            "customer_id",
            null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── GetOwnershipSpecification (convenience overload with default customer_id) ──
    [Fact]
    public void GetOwnershipSpecification_ConvenienceOverload_AdminReturnsNull()
    {
        _currentUserService.Setup(s => s.Role).Returns("Admin");

        var result = OwnershipHelper.GetOwnershipSpecification(
            _currentUserService.Object,
            id => new TestOwnerSpecification(id));

        result.Should().BeNull();
    }

    [Fact]
    public void GetOwnershipSpecification_ConvenienceOverload_NonAdminWithClaim_ReturnsSpecification()
    {
        _currentUserService.Setup(s => s.Role).Returns("Customer");
        _currentUserService.Setup(s => s.GetClaimValue<int>("customer_id")).Returns(99);

        var result = OwnershipHelper.GetOwnershipSpecification(
            _currentUserService.Object,
            id => new TestOwnerSpecification(id));

        result.Should().NotBeNull();
        result!.CustomerId.Should().Be(99);
    }
}
