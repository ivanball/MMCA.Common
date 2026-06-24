using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Specifications;
using MMCA.Common.Domain.Entities;
using Moq;

namespace MMCA.Common.Application.Tests.Specifications;

public sealed class CrossSourceSpecificationTests
{
    public sealed class Dependent : AuditableBaseEntity<int>
    {
        public int PrincipalId { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public sealed class Principal : AuditableBaseEntity<int>
    {
        public bool IsActive { get; set; }
    }

    private static Mock<IUnitOfWork> UnitOfWorkReturning(params int[] matchingPrincipalKeys)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var principalRepo = new Mock<IReadRepository<Principal, int>>();
        unitOfWork.Setup(u => u.GetReadRepository<Principal, int>()).Returns(principalRepo.Object);

        principalRepo
            .Setup(r => r.GetProjectedAsync(
                It.IsAny<Expression<Func<Principal, int>>>(),
                It.IsAny<Expression<Func<Principal, bool>>>(),
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(matchingPrincipalKeys);

        return unitOfWork;
    }

    [Fact]
    public async Task BuildAsync_FiltersDependentsByCrossSourcePrincipalKeys()
    {
        var unitOfWork = UnitOfWorkReturning(1, 2);

        var spec = await CrossSourceSpecification.BuildAsync<Dependent, int, Principal, int>(
            unitOfWork.Object,
            principalPredicate: p => p.IsActive,
            dependentForeignKey: d => d.PrincipalId,
            cancellationToken: CancellationToken.None);

        var predicate = spec.Criteria.Compile();

        predicate(new Dependent { Id = 10, PrincipalId = 1 }).Should().BeTrue();
        predicate(new Dependent { Id = 11, PrincipalId = 2 }).Should().BeTrue();
        predicate(new Dependent { Id = 12, PrincipalId = 3 }).Should().BeFalse("principal 3 did not match the predicate");
    }

    [Fact]
    public async Task BuildAsync_AndsTheLocalPredicate()
    {
        var unitOfWork = UnitOfWorkReturning(1);

        var spec = await CrossSourceSpecification.BuildAsync<Dependent, int, Principal, int>(
            unitOfWork.Object,
            principalPredicate: p => p.IsActive,
            dependentForeignKey: d => d.PrincipalId,
            localPredicate: d => d.Name != "skip",
            cancellationToken: CancellationToken.None);

        var predicate = spec.Criteria.Compile();

        predicate(new Dependent { Id = 10, PrincipalId = 1, Name = "ok" }).Should().BeTrue();
        predicate(new Dependent { Id = 11, PrincipalId = 1, Name = "skip" }).Should().BeFalse("the local predicate excludes it");
        predicate(new Dependent { Id = 12, PrincipalId = 9, Name = "ok" }).Should().BeFalse("the FK is not among the matching keys");
    }

    [Fact]
    public async Task BuildAsync_NoMatchingPrincipals_ExcludesEverything()
    {
        var unitOfWork = UnitOfWorkReturning();

        var spec = await CrossSourceSpecification.BuildAsync<Dependent, int, Principal, int>(
            unitOfWork.Object,
            principalPredicate: p => p.IsActive,
            dependentForeignKey: d => d.PrincipalId,
            cancellationToken: CancellationToken.None);

        var predicate = spec.Criteria.Compile();

        predicate(new Dependent { Id = 10, PrincipalId = 1 }).Should().BeFalse();
    }
}
