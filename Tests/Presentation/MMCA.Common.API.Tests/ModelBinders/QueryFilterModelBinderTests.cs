using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MMCA.Common.API.ModelBinders;
using Moq;

namespace MMCA.Common.API.Tests.ModelBinders;

public sealed class QueryFilterModelBinderTests
{
    private readonly QueryFilterModelBinder _sut = new();

    private static DefaultModelBindingContext CreateBindingContext(QueryString queryString)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = queryString;

        var bindingContext = new Mock<DefaultModelBindingContext> { CallBase = true };
        var realContext = bindingContext.Object;
        realContext.ActionContext = new Microsoft.AspNetCore.Mvc.ActionContext(
            httpContext,
            new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());

        return realContext;
    }

    [Fact]
    public async Task BindModelAsync_WithCompleteFilter_ShouldParseCorrectly()
    {
        var context = CreateBindingContext(
            new QueryString("?filters[name].operator=eq&filters[name].value=TestProduct"));

        await _sut.BindModelAsync(context);

        context.Result.IsModelSet.Should().BeTrue();
        var filters = context.Result.Model as Dictionary<string, (string Operator, string Value)>;
        filters.Should().NotBeNull();
        filters.Should().ContainKey("name");
        filters!["name"].Operator.Should().Be("eq");
        filters["name"].Value.Should().Be("TestProduct");
    }

    [Fact]
    public async Task BindModelAsync_WithMultipleFilters_ShouldParseAll()
    {
        var context = CreateBindingContext(new QueryString(
            "?filters[name].operator=eq&filters[name].value=Test&filters[price].operator=gt&filters[price].value=100"));

        await _sut.BindModelAsync(context);

        context.Result.IsModelSet.Should().BeTrue();
        var filters = context.Result.Model as Dictionary<string, (string Operator, string Value)>;
        filters.Should().HaveCount(2);
    }

    [Fact]
    public async Task BindModelAsync_WithNoFilterKeys_ShouldReturnEmptyDictionary()
    {
        var context = CreateBindingContext(new QueryString("?page=1&pageSize=10"));

        await _sut.BindModelAsync(context);

        context.Result.IsModelSet.Should().BeTrue();
        var filters = context.Result.Model as Dictionary<string, (string Operator, string Value)>;
        filters.Should().NotBeNull();
        filters.Should().BeEmpty();
    }

    [Fact]
    public async Task BindModelAsync_WithOnlyOperator_ShouldRemoveIncompleteFilter()
    {
        var context = CreateBindingContext(new QueryString("?filters[name].operator=eq"));

        await _sut.BindModelAsync(context);

        context.Result.IsModelSet.Should().BeTrue();
        var filters = context.Result.Model as Dictionary<string, (string Operator, string Value)>;
        filters.Should().BeEmpty();
    }

    [Fact]
    public async Task BindModelAsync_WithOnlyValue_ShouldRemoveIncompleteFilter()
    {
        var context = CreateBindingContext(new QueryString("?filters[name].value=Test"));

        await _sut.BindModelAsync(context);

        context.Result.IsModelSet.Should().BeTrue();
        var filters = context.Result.Model as Dictionary<string, (string Operator, string Value)>;
        filters.Should().BeEmpty();
    }

    [Fact]
    public async Task BindModelAsync_WithEmptyQueryString_ShouldReturnEmptyDictionary()
    {
        var context = CreateBindingContext(new QueryString(string.Empty));

        await _sut.BindModelAsync(context);

        context.Result.IsModelSet.Should().BeTrue();
        var filters = context.Result.Model as Dictionary<string, (string Operator, string Value)>;
        filters.Should().BeEmpty();
    }

    [Fact]
    public void BindModelAsync_NullBindingContext_ShouldThrowArgumentNullException() =>
        FluentActions.Invoking(() => _sut.BindModelAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();

    [Fact]
    public async Task BindModelAsync_CaseInsensitiveFilterKeys_ShouldParse()
    {
        var context = CreateBindingContext(
            new QueryString("?Filters[Name].Operator=eq&Filters[Name].Value=Test"));

        await _sut.BindModelAsync(context);

        context.Result.IsModelSet.Should().BeTrue();
        var filters = context.Result.Model as Dictionary<string, (string Operator, string Value)>;
        filters.Should().NotBeNull();
        filters.Should().NotBeEmpty();
    }
}
