using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services.Navigation;
using MMCA.Common.Domain.Entities;
using Moq;

namespace MMCA.Common.Application.Tests.Services;

public sealed class FKNavigationDescriptorTests
{
    public class ParentEntity : AuditableBaseEntity<int>
    {
        public int? RelatedId { get; set; }
    }

    public class RelatedEntity : AuditableBaseEntity<int>;

    private static FKNavigationDescriptor<ParentEntity, RelatedEntity, int> CreateDescriptor(
        string propertyName = "Related",
        Func<ParentEntity, int?>? parentKeySelector = null,
        Action<ParentEntity, List<RelatedEntity>>? assignAction = null) =>
        new()
        {
            PropertyName = propertyName,
            ParentKeySelector = parentKeySelector ?? (p => p.RelatedId),
            ChildForeignKeySelector = c => c.Id,
            AssignAction = assignAction ?? ((_, _) => { })
        };

    [Fact]
    public void RequiresChildren_ReturnsFalse() =>
        CreateDescriptor().RequiresChildren.Should().BeFalse();

    [Fact]
    public void PropertyName_ReturnsConfiguredValue()
    {
        FKNavigationDescriptor<ParentEntity, RelatedEntity, int> descriptor = CreateDescriptor("CategoryNav");

        descriptor.PropertyName.Should().Be("CategoryNav");
    }

    [Fact]
    public void ImplementsINavigationDescriptor() =>
        CreateDescriptor().Should().BeAssignableTo<INavigationDescriptor<ParentEntity>>();

    [Fact]
    public void ParentKeySelector_ExtractsNullableFKFromParent()
    {
        var parent = new ParentEntity { Id = 1, RelatedId = 42 };
        FKNavigationDescriptor<ParentEntity, RelatedEntity, int> descriptor = CreateDescriptor();

        int? result = descriptor.ParentKeySelector(parent);

        result.Should().Be(42);
    }

    [Fact]
    public void ParentKeySelector_ReturnsNullWhenFKIsNull()
    {
        var parent = new ParentEntity { Id = 1, RelatedId = null };
        FKNavigationDescriptor<ParentEntity, RelatedEntity, int> descriptor = CreateDescriptor();

        int? result = descriptor.ParentKeySelector(parent);

        result.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_DelegatesToNavigationLoader()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var readRepo = new Mock<IReadRepository<RelatedEntity, int>>();
        unitOfWork.Setup(u => u.GetReadRepository<RelatedEntity, int>()).Returns(readRepo.Object);

        readRepo
            .Setup(r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<System.Linq.Expressions.Expression<Func<RelatedEntity, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var parent = new ParentEntity { Id = 1, RelatedId = 10 };
        IReadOnlyCollection<ParentEntity> entities = [parent];
        FKNavigationDescriptor<ParentEntity, RelatedEntity, int> descriptor = CreateDescriptor();

        await descriptor.LoadAsync(entities, unitOfWork.Object, CancellationToken.None);

        unitOfWork.Verify(u => u.GetReadRepository<RelatedEntity, int>(), Times.Once);
    }

    [Fact]
    public async Task LoadAsync_WithNullFKs_DoesNotQueryRepository()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var readRepo = new Mock<IReadRepository<RelatedEntity, int>>();
        unitOfWork.Setup(u => u.GetReadRepository<RelatedEntity, int>()).Returns(readRepo.Object);

        var parent = new ParentEntity { Id = 1, RelatedId = null };
        IReadOnlyCollection<ParentEntity> entities = [parent];
        FKNavigationDescriptor<ParentEntity, RelatedEntity, int> descriptor = CreateDescriptor();

        await descriptor.LoadAsync(entities, unitOfWork.Object, CancellationToken.None);

        readRepo.Verify(
            r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<System.Linq.Expressions.Expression<Func<RelatedEntity, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadAsync_AssignsLoadedEntitiesToParent()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var readRepo = new Mock<IReadRepository<RelatedEntity, int>>();
        unitOfWork.Setup(u => u.GetReadRepository<RelatedEntity, int>()).Returns(readRepo.Object);

        var related = new RelatedEntity { Id = 10 };
        readRepo
            .Setup(r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<System.Linq.Expressions.Expression<Func<RelatedEntity, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([related]);

        var parent = new ParentEntity { Id = 1, RelatedId = 10 };
        IReadOnlyCollection<ParentEntity> entities = [parent];

        List<RelatedEntity>? assignedEntities = null;
        FKNavigationDescriptor<ParentEntity, RelatedEntity, int> descriptor = CreateDescriptor(
            assignAction: (_, list) => assignedEntities = list);

        await descriptor.LoadAsync(entities, unitOfWork.Object, CancellationToken.None);

        assignedEntities.Should().NotBeNull();
        assignedEntities.Should().ContainSingle().Which.Id.Should().Be(10);
    }
}
