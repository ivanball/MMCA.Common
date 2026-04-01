using AwesomeAssertions;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Domain.Tests.Entities;

public sealed class AuditableBaseEntityAdditionalTests
{
    private sealed class UndeletableEntity : AuditableBaseEntity<int>
    {
        public string Name { get; set; } = string.Empty;

        public Shared.Abstractions.Result TryUndelete() => Undelete();
    }

    // ── Undelete ──
    [Fact]
    public void Undelete_WhenDeleted_RestoresEntity()
    {
        var entity = new UndeletableEntity { Id = 1, Name = "Test" };
        entity.Delete();
        entity.IsDeleted.Should().BeTrue();

        var result = entity.TryUndelete();

        result.IsSuccess.Should().BeTrue();
        entity.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Undelete_WhenNotDeleted_ReturnsFailure()
    {
        var entity = new UndeletableEntity { Id = 1, Name = "Test" };

        var result = entity.TryUndelete();

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be("Entity.NotDeleted");
    }

    // ── Delete idempotency ──
    [Fact]
    public void Delete_WhenAlreadyDeleted_ReturnsFailure()
    {
        var entity = new UndeletableEntity { Id = 1, Name = "Test" };
        entity.Delete();

        var result = entity.Delete();

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Delete_ThenUndelete_ThenDelete_Works()
    {
        var entity = new UndeletableEntity { Id = 1, Name = "Test" };

        entity.Delete().IsSuccess.Should().BeTrue();
        entity.TryUndelete().IsSuccess.Should().BeTrue();
        entity.Delete().IsSuccess.Should().BeTrue();
        entity.IsDeleted.Should().BeTrue();
    }

    // ── Audit fields initial state ──
    [Fact]
    public void AuditFields_DefaultValues()
    {
        var entity = new UndeletableEntity { Id = 1 };

        entity.IsDeleted.Should().BeFalse();
        entity.CreatedOn.Should().Be(default);
        entity.CreatedBy.Should().Be(default);
        entity.LastModifiedOn.Should().BeNull();
        entity.LastModifiedBy.Should().BeNull();
        entity.RowVersion.Should().BeEmpty();
    }
}
