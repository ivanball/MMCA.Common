using System.Linq.Expressions;
using AwesomeAssertions;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Specifications;

namespace MMCA.Common.Domain.Tests.Specifications;

public sealed class OwnedByUserSpecificationTests
{
    // CreatedBy is stamped by the infrastructure layer through EF's change tracker, so the
    // fake overrides the virtual getter to make the audit field settable from a test.
    private sealed class FakeAnswer : AuditableBaseEntity<int>
    {
        private UserIdentifierType _createdBy;

        public override UserIdentifierType CreatedBy => _createdBy;

        public void StampCreatedBy(UserIdentifierType userId) => _createdBy = userId;
    }

    private static FakeAnswer CreateAnswer(int id, UserIdentifierType createdBy)
    {
        var answer = new FakeAnswer { Id = id };
        answer.StampCreatedBy(createdBy);
        return answer;
    }

    // ── IsSatisfiedBy ──
    [Fact]
    public void IsSatisfiedBy_OwnedByTheSameUser_ReturnsTrue()
    {
        var spec = new OwnedByUserSpecification<FakeAnswer, int>(7);

        spec.IsSatisfiedBy(CreateAnswer(1, 7)).Should().BeTrue();
    }

    [Fact]
    public void IsSatisfiedBy_OwnedByAnotherUser_ReturnsFalse()
    {
        var spec = new OwnedByUserSpecification<FakeAnswer, int>(7);

        spec.IsSatisfiedBy(CreateAnswer(1, 8)).Should().BeFalse();
    }

    [Fact]
    public void UserId_ExposesTheConstructorArgument()
    {
        var spec = new OwnedByUserSpecification<FakeAnswer, int>(42);

        spec.UserId.Should().Be(42);
    }

    // ── Criteria stays an EF-translatable expression over the mapped audit column ──
    [Fact]
    public void Criteria_ComparesTheCreatedByPropertyDeclaredOnTheEntity()
    {
        var spec = new OwnedByUserSpecification<FakeAnswer, int>(7);

        Expression<Func<FakeAnswer, bool>> criteria = spec.Criteria;

        var comparison = criteria.Body.Should().BeAssignableTo<BinaryExpression>().Subject;
        comparison.NodeType.Should().Be(ExpressionType.Equal);

        var member = comparison.Left.Should().BeAssignableTo<MemberExpression>().Subject;
        member.Member.Name.Should().Be(nameof(FakeAnswer.CreatedBy));
        member.Expression.Should().BeAssignableTo<ParameterExpression>();
        typeof(FakeAnswer).Should().BeAssignableTo(member.Member.DeclaringType!);
    }

    // ── Composition with the existing operators ──
    [Fact]
    public void Composition_WithNotSpecification_InvertsOwnership()
    {
        var ownSpec = new OwnedByUserSpecification<FakeAnswer, int>(7);
        var notOwnSpec = new NotSpecification<FakeAnswer, int>(ownSpec);

        notOwnSpec.IsSatisfiedBy(CreateAnswer(1, 7)).Should().BeFalse();
        notOwnSpec.IsSatisfiedBy(CreateAnswer(2, 8)).Should().BeTrue();
    }

    // ── LINQ filtering, the same shape EF Core translates to SQL ──
    [Fact]
    public void Criteria_FiltersAQueryableToTheOwningUsersRows()
    {
        var spec = new OwnedByUserSpecification<FakeAnswer, int>(7);

        FakeAnswer[] answers =
        [
            CreateAnswer(1, 7),
            CreateAnswer(2, 8),
            CreateAnswer(3, 7)
        ];

        var owned = answers.AsQueryable().Where(spec.Criteria).ToList();

        owned.Should().HaveCount(2);
        owned.Should().OnlyContain(a => a.CreatedBy == 7);
        owned.Should().Contain(a => a.Id == 1);
        owned.Should().Contain(a => a.Id == 3);
    }
}
