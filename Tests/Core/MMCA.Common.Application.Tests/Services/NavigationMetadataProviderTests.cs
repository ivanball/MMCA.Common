using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Entities;
using Moq;

namespace MMCA.Common.Application.Tests.Services;

public sealed class NavigationMetadataProviderTests
{
    // Each scenario needs unique entity types to avoid collision in the static cache.
    public class SupportedFK : AuditableBaseEntity<int>
    {
        [Navigation(IsCollection = false)]
        public RelatedA? Related { get; set; }
    }

    public class UnsupportedFK : AuditableBaseEntity<int>
    {
        [Navigation(IsCollection = false)]
        public RelatedB? Related { get; set; }
    }

    public class SupportedChild : AuditableBaseEntity<int>
    {
        [Navigation(IsCollection = true)]
        public ICollection<ChildA> Children { get; set; } = [];
    }

    public class UnsupportedChild : AuditableBaseEntity<int>
    {
        [Navigation(IsCollection = true)]
        public ICollection<ChildB> Children { get; set; } = [];
    }

    public class MixedEntity : AuditableBaseEntity<int>
    {
        [Navigation(IsCollection = false)]
        public RelatedC? FKNav { get; set; }

        [Navigation(IsCollection = true)]
        public ICollection<ChildC> ChildNav { get; set; } = [];
    }

    public class NoNavEntity : AuditableBaseEntity<int>
    {
        public string? PlainProperty { get; set; }
    }

    public class ReadOnlyCollectionEntity : AuditableBaseEntity<int>
    {
        [Navigation(IsCollection = true)]
        public IReadOnlyCollection<ChildD> Children { get; set; } = [];
    }

    public class RelatedA : AuditableBaseEntity<int>;

    public class RelatedB : AuditableBaseEntity<int>;

    public class RelatedC : AuditableBaseEntity<int>;

    public class ChildA : AuditableBaseEntity<int>;

    public class ChildB : AuditableBaseEntity<int>;

    public class ChildC : AuditableBaseEntity<int>;

    public class ChildD : AuditableBaseEntity<int>;

    [Fact]
    public void BuildIncludes_WithSupportedFK_ReturnsInSupportedList()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        dataSourceService
            .Setup(d => d.HaveIncludeSupport(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<SupportedFK>(includeFKs: true, includeChildren: false);

        result.SupportedIncludes.Should().ContainSingle()
            .Which.PropertyName.Should().Be("Related");
        result.UnsupportedIncludes.Should().BeEmpty();
    }

    [Fact]
    public void BuildIncludes_WithUnsupportedFK_ReturnsInUnsupportedList()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        dataSourceService
            .Setup(d => d.HaveIncludeSupport(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<UnsupportedFK>(includeFKs: true, includeChildren: false);

        result.UnsupportedIncludes.Should().ContainSingle()
            .Which.PropertyName.Should().Be("Related");
        result.SupportedIncludes.Should().BeEmpty();
    }

    [Fact]
    public void BuildIncludes_WithSupportedChild_ReturnsInSupportedList()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        dataSourceService
            .Setup(d => d.HaveIncludeSupport(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<SupportedChild>(includeFKs: false, includeChildren: true);

        result.SupportedIncludes.Should().ContainSingle()
            .Which.PropertyName.Should().Be("Children");
        result.UnsupportedIncludes.Should().BeEmpty();
    }

    [Fact]
    public void BuildIncludes_WithUnsupportedChild_ReturnsInUnsupportedList()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        dataSourceService
            .Setup(d => d.HaveIncludeSupport(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<UnsupportedChild>(includeFKs: false, includeChildren: true);

        result.UnsupportedIncludes.Should().ContainSingle()
            .Which.PropertyName.Should().Be("Children");
        result.SupportedIncludes.Should().BeEmpty();
    }

    [Fact]
    public void BuildIncludes_WithBothFKAndChildren_ReturnsBothNavigations()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        dataSourceService
            .Setup(d => d.HaveIncludeSupport(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<MixedEntity>(includeFKs: true, includeChildren: true);

        result.SupportedIncludes.Should().HaveCount(2);
        result.SupportedIncludes.Should().Contain(n => n.PropertyName == "FKNav");
        result.SupportedIncludes.Should().Contain(n => n.PropertyName == "ChildNav");
    }

    [Fact]
    public void BuildIncludes_WithNeitherFKNorChildren_ReturnsEmptyMetadata()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<MixedEntity>(includeFKs: false, includeChildren: false);

        result.SupportedIncludes.Should().BeEmpty();
        result.UnsupportedIncludes.Should().BeEmpty();
    }

    [Fact]
    public void BuildIncludes_WithNoNavigationAttributes_ReturnsEmptyMetadata()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<NoNavEntity>(includeFKs: true, includeChildren: true);

        result.SupportedIncludes.Should().BeEmpty();
        result.UnsupportedIncludes.Should().BeEmpty();
    }

    [Fact]
    public void BuildIncludes_FKNavigation_ClassifiesAsForeignKey()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        dataSourceService
            .Setup(d => d.HaveIncludeSupport(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<SupportedFK>(includeFKs: true, includeChildren: false);

        result.SupportedIncludes.Should().ContainSingle()
            .Which.Type.Should().Be(NavigationType.ForeignKey);
    }

    [Fact]
    public void BuildIncludes_ChildNavigation_ClassifiesAsChildCollection()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        dataSourceService
            .Setup(d => d.HaveIncludeSupport(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<SupportedChild>(includeFKs: false, includeChildren: true);

        result.SupportedIncludes.Should().ContainSingle()
            .Which.Type.Should().Be(NavigationType.ChildCollection);
    }

    [Fact]
    public void BuildIncludes_WithReadOnlyCollection_UnwrapsGenericTypeCorrectly()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        dataSourceService
            .Setup(d => d.HaveIncludeSupport(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<ReadOnlyCollectionEntity>(includeFKs: false, includeChildren: true);

        result.SupportedIncludes.Should().ContainSingle()
            .Which.TargetEntityType.Should().Be<ChildD>();
    }

    [Fact]
    public void BuildIncludes_OnlyFKsRequested_DoesNotIncludeChildNavigations()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        dataSourceService
            .Setup(d => d.HaveIncludeSupport(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<MixedEntity>(includeFKs: true, includeChildren: false);

        result.SupportedIncludes.Should().ContainSingle()
            .Which.PropertyName.Should().Be("FKNav");
    }

    [Fact]
    public void BuildIncludes_OnlyChildrenRequested_DoesNotIncludeFKNavigations()
    {
        var dataSourceService = new Mock<IDataSourceService>();
        dataSourceService
            .Setup(d => d.HaveIncludeSupport(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var sut = new NavigationMetadataProvider(dataSourceService.Object);

        NavigationMetadata result = sut.BuildIncludes<MixedEntity>(includeFKs: false, includeChildren: true);

        result.SupportedIncludes.Should().ContainSingle()
            .Which.PropertyName.Should().Be("ChildNav");
    }
}
