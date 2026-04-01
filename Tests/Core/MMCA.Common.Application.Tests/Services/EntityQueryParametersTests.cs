using System.Collections.Frozen;
using AwesomeAssertions;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.Tests.Services;

/// <summary>
/// Tests for <see cref="EntityQueryParameters{TEntity}"/> verifying default values
/// and property initialization.
/// </summary>
public sealed class EntityQueryParametersTests
{
    private sealed class TestEntity : AuditableBaseEntity<int>
    {
        public string Name { get; init; } = string.Empty;
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var sut = new EntityQueryParameters<TestEntity>();

        sut.Criteria.Should().BeNull();
        sut.Filters.Should().BeNull();
        sut.SortColumn.Should().BeNull();
        sut.SortDirection.Should().BeNull();
        sut.Fields.Should().BeNull();
        sut.PageNumber.Should().BeNull();
        sut.PageSize.Should().BeNull();
        sut.IncludeFKs.Should().BeFalse();
        sut.IncludeChildren.Should().BeFalse();
        sut.DTOToEntityPropertyMap.Should().BeEmpty();
    }

    [Fact]
    public void DTOToEntityPropertyMap_DefaultsToFrozenEmpty()
    {
        var sut = new EntityQueryParameters<TestEntity>();

        sut.DTOToEntityPropertyMap.Should().BeAssignableTo<FrozenDictionary<string, string>>();
    }

    [Fact]
    public void Pagination_CanBeSet()
    {
        var sut = new EntityQueryParameters<TestEntity>
        {
            PageNumber = 3,
            PageSize = 25
        };

        sut.PageNumber.Should().Be(3);
        sut.PageSize.Should().Be(25);
    }

    [Fact]
    public void SortProperties_CanBeSet()
    {
        var sut = new EntityQueryParameters<TestEntity>
        {
            SortColumn = "Name",
            SortDirection = "desc"
        };

        sut.SortColumn.Should().Be("Name");
        sut.SortDirection.Should().Be("desc");
    }

    [Fact]
    public void Filters_CanBeSet()
    {
        var filters = new Dictionary<string, (string Operator, string Value)>
        {
            ["Name"] = ("EQUALS", "Test")
        };

        var sut = new EntityQueryParameters<TestEntity>
        {
            Filters = filters
        };

        sut.Filters.Should().HaveCount(1);
        sut.Filters!["Name"].Operator.Should().Be("EQUALS");
    }

    [Fact]
    public void Criteria_CanBeSet()
    {
        var sut = new EntityQueryParameters<TestEntity>
        {
            Criteria = e => e.Name == "Test"
        };

        sut.Criteria.Should().NotBeNull();
    }

    [Fact]
    public void NavigationFlags_CanBeSet()
    {
        var sut = new EntityQueryParameters<TestEntity>
        {
            IncludeFKs = true,
            IncludeChildren = true
        };

        sut.IncludeFKs.Should().BeTrue();
        sut.IncludeChildren.Should().BeTrue();
    }

    [Fact]
    public void DTOToEntityPropertyMap_CanBeCustomized()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CategoryName"] = "Category.Name"
        };

        var sut = new EntityQueryParameters<TestEntity>
        {
            DTOToEntityPropertyMap = map
        };

        sut.DTOToEntityPropertyMap.Should().HaveCount(1);
        sut.DTOToEntityPropertyMap["CategoryName"].Should().Be("Category.Name");
    }

    [Fact]
    public void Fields_CanBeSet()
    {
        var sut = new EntityQueryParameters<TestEntity>
        {
            Fields = "Id,Name"
        };

        sut.Fields.Should().Be("Id,Name");
    }

    [Fact]
    public void Record_SupportsWithExpression()
    {
        var original = new EntityQueryParameters<TestEntity>
        {
            PageNumber = 1,
            PageSize = 10,
        };

        var modified = original with { PageNumber = 2 };

        modified.PageNumber.Should().Be(2);
        modified.PageSize.Should().Be(10);
    }
}
