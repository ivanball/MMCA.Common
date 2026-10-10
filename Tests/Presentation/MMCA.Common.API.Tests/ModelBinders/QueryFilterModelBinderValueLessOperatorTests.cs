using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MMCA.Common.API.ModelBinders;
using MMCA.Common.Application.Services.Filtering;
using Moq;

namespace MMCA.Common.API.Tests.ModelBinders;

/// <summary>
/// Pins the value-less operators (grid-filter audit, run 9, item A). IS EMPTY and IS NOT EMPTY take no
/// value, so a grid sends <c>filters[X].operator=IS EMPTY</c> with no <c>.value</c> key at all; the
/// binder used to discard every entry without a value, so the filter never reached the server and
/// the grid returned every row. A value-REQUIRING operator with no value is still discarded. The
/// end-to-end cases bind the query string and run the result through <see cref="QueryFilterService"/>,
/// so "kept by the binder" is proven to mean "applied by the server".
/// </summary>
public sealed class QueryFilterModelBinderValueLessOperatorTests
{
    private static readonly Dictionary<string, string> EmptyMap = [];

    private readonly QueryFilterModelBinder _sut = new();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("S1144", "S1144:Unused private types or members should be removed", Justification = "Properties are used via reflection by QueryFilterService")]
    private sealed class Row
    {
        public int Id { get; set; }

        public string? Name { get; set; }

        public int? Score { get; set; }
    }

    private static IQueryable<Row> Rows() =>
        new List<Row>
        {
            new() { Id = 1, Name = "Ada", Score = 10 },
            new() { Id = 2, Name = null, Score = null },
            new() { Id = 3, Name = string.Empty, Score = 30 },
            new() { Id = 4, Name = "Grace", Score = null },
        }.AsQueryable();

    private static DefaultModelBindingContext CreateBindingContext(string queryString)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(queryString);

        var bindingContext = new Mock<DefaultModelBindingContext> { CallBase = true };
        var realContext = bindingContext.Object;
        realContext.ActionContext = new Microsoft.AspNetCore.Mvc.ActionContext(
            httpContext,
            new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());

        return realContext;
    }

    private async Task<Dictionary<string, (string Operator, string Value)>> BindAsync(string queryString)
    {
        var context = CreateBindingContext(queryString);
        await _sut.BindModelAsync(context);
        context.Result.IsModelSet.Should().BeTrue();
        return (Dictionary<string, (string Operator, string Value)>)context.Result.Model!;
    }

    // == The binder keeps an operator-only entry for a value-less operator ==
    [Theory]
    [InlineData("IS%20EMPTY", "IS EMPTY")]
    [InlineData("IS%20NOT%20EMPTY", "IS NOT EMPTY")]
    [InlineData("is%20empty", "is empty")]
    [InlineData("is%20not%20empty", "is not empty")]
    public async Task BindModelAsync_ValueLessOperatorWithNoValue_KeepsTheEntry(string encodedOperator, string expectedOperator)
    {
        var filters = await BindAsync($"?filters[Name].operator={encodedOperator}");

        filters.Should().ContainKey("Name", "IS EMPTY / IS NOT EMPTY take no value, so the operator alone is a complete filter");
        filters["Name"].Operator.Should().Be(expectedOperator);
        filters["Name"].Value.Should().BeEmpty();
    }

    [Fact]
    public async Task BindModelAsync_ValueLessOperatorWithABlankValue_KeepsTheEntry()
    {
        var filters = await BindAsync("?filters[Name].operator=IS%20EMPTY&filters[Name].value=");

        filters.Should().ContainKey("Name");
        filters["Name"].Operator.Should().Be("IS EMPTY");
    }

    [Theory]
    [InlineData("contains")]
    [InlineData("EQUALS")]
    [InlineData("GREATER%20THAN")]
    public async Task BindModelAsync_ValueRequiringOperatorWithNoValue_StillDiscardsTheEntry(string encodedOperator)
    {
        var filters = await BindAsync($"?filters[Name].operator={encodedOperator}");

        filters.Should().BeEmpty("an operator that compares against a value is incomplete without one");
    }

    [Fact]
    public async Task BindModelAsync_ValueLessAndValueRequiringTogether_KeepsOnlyTheCompleteOnes()
    {
        var filters = await BindAsync(
            "?filters[Name].operator=IS%20NOT%20EMPTY&filters[Score].operator=GREATER%20THAN&filters[Id].operator=EQUALS&filters[Id].value=3");

        filters.Keys.Should().BeEquivalentTo("Name", "Id");
    }

    // == End to end: bound, validated, applied ==
    [Fact]
    public async Task EndToEnd_IsEmptyOnAString_ReturnsOnlyNullOrEmptyRows()
    {
        var filters = await BindAsync("?filters[Name].operator=IS%20EMPTY");

        QueryFilterService.ValidateFilters<Row>(filters, EmptyMap).IsSuccess.Should().BeTrue();
        QueryFilterService.ApplyFilters(Rows(), filters, EmptyMap)
            .Select(r => r.Id)
            .Should().Equal(2, 3);
    }

    [Fact]
    public async Task EndToEnd_MudBlazorIsNotEmptyOnAString_ReturnsOnlyRowsWithAName()
    {
        var filters = await BindAsync("?filters[Name].operator=is%20not%20empty");

        QueryFilterService.ValidateFilters<Row>(filters, EmptyMap).IsSuccess.Should().BeTrue();
        QueryFilterService.ApplyFilters(Rows(), filters, EmptyMap)
            .Select(r => r.Id)
            .Should().Equal(1, 4);
    }

    [Fact]
    public async Task EndToEnd_IsEmptyOnANullableNumber_ReturnsOnlyNullRows()
    {
        var filters = await BindAsync("?filters[Score].operator=IS%20EMPTY");

        QueryFilterService.ValidateFilters<Row>(filters, EmptyMap).IsSuccess.Should().BeTrue();
        QueryFilterService.ApplyFilters(Rows(), filters, EmptyMap)
            .Select(r => r.Id)
            .Should().Equal(2, 4);
    }
}
