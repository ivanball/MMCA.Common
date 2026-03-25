using FluentAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Infrastructure.Tests;

public sealed class UseDataSourceAttributeTests
{
    [Theory]
    [InlineData(DataSource.CosmosDB)]
    [InlineData(DataSource.Sqlite)]
    [InlineData(DataSource.SQLServer)]
    public void Constructor_SetsDataSourceProperty(DataSource dataSource)
    {
        var attribute = new UseDataSourceAttribute(dataSource);

        attribute.DataSource.Should().Be(dataSource);
    }
}
