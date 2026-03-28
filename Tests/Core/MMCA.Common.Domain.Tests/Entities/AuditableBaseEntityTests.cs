using AwesomeAssertions;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Domain.Tests.Entities;

public class AuditableBaseEntityTests
{
    private sealed class TestEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    // ── Delete ──
    [Fact]
    public void Delete_WhenNotDeleted_ReturnsSuccessAndSetsIsDeleted()
    {
        var entity = new TestEntity { Id = 1, Name = "Test" };

        var result = entity.Delete();

        result.IsSuccess.Should().BeTrue();
        entity.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public void Delete_WhenAlreadyDeleted_ReturnsFailure()
    {
        var entity = new TestEntity { Id = 1, Name = "Test" };
        entity.Delete(); // mark as deleted via domain method

        var result = entity.Delete();

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Error.AlreadyDeleted");
    }

    [Fact]
    public void Delete_WhenAlreadyDeleted_DoesNotChangeState()
    {
        var entity = new TestEntity { Id = 1, Name = "Test" };
        entity.Delete(); // mark as deleted via domain method

        entity.Delete();

        entity.IsDeleted.Should().BeTrue();
    }

    // ── Audit properties ──
    [Fact]
    public void AuditProperties_DefaultToExpectedValues()
    {
        var entity = new TestEntity { Id = 1 };

        entity.CreatedOn.Should().Be(default);
        entity.CreatedBy.Should().Be(0);
        entity.LastModifiedOn.Should().BeNull();
        entity.LastModifiedBy.Should().BeNull();
        entity.IsDeleted.Should().BeFalse();
    }

}
