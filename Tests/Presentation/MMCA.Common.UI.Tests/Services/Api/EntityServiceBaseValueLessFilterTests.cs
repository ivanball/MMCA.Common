using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Api;

/// <summary>
/// Pins the client half of the value-less operators (grid-filter audit, run 9, item A):
/// <see cref="EntityServiceBase{TEntityDTO, TId}.GetPagedAsync"/> sends
/// <c>filters[X].operator=IS EMPTY</c> (and IS NOT EMPTY, in either casing) with a blank value, and
/// sends no <c>.value</c> key for it, so the server binder sees an operator-only entry. The server
/// half (the binder keeping that entry) is pinned in <c>QueryFilterModelBinderValueLessOperatorTests</c>.
/// </summary>
public sealed class EntityServiceBaseValueLessFilterTests
{
    private sealed record WidgetDto : IBaseDTO<int>
    {
        public required int Id { get; init; }
    }

    private sealed class WidgetService(IHttpClientFactory httpClientFactory, ITokenStorageService tokenStorageService)
        : EntityServiceBase<WidgetDto, int>("widgets", httpClientFactory, tokenStorageService);

    private static (WidgetService Sut, StubHttpMessageHandler Handler) CreateSut()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PagedCollectionResult<WidgetDto>([], new PaginationMetadata(0, 10, 1))),
        });
        var tokenStorage = new Mock<ITokenStorageService>();
        tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync("stored-access-token");
        return (new WidgetService(new StubHttpClientFactory(handler), tokenStorage.Object), handler);
    }

    [Theory]
    [InlineData("IS EMPTY", "IS%20EMPTY", "")]
    [InlineData("IS NOT EMPTY", "IS%20NOT%20EMPTY", "")]
    [InlineData("is empty", "is%20empty", " ")]
    [InlineData("is not empty", "is%20not%20empty", "")]
    public async Task GetPagedAsync_ValueLessOperatorWithABlankValue_SendsTheOperatorAlone(
        string op, string encodedOp, string blankValue)
    {
        var (sut, handler) = CreateSut();
        var filters = new Dictionary<string, (string Operator, string Value)>
        {
            ["Name"] = (op, blankValue),
        };

        await sut.GetPagedAsync(
            filters, pageNumber: 1, pageSize: 10, sortColumn: null, sortDirection: null,
            includeChildren: false, TestContext.Current.CancellationToken);

        var query = handler.LastRequest.Uri!.PathAndQuery;
        query.Should().Contain($"filters[Name].operator={encodedOp}");
        query.Should().NotContain("filters[Name].value", "a value-less operator carries no value key");
    }
}
