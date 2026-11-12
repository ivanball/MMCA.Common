using System.Net;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auditing;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auditing;

/// <summary>
/// Pins <see cref="AuditHistoryService.GetPageAsync"/> (item 12, UI side): one bearer-authenticated
/// GET to <c>{prefix}/{alias}/{key}?page=&amp;pageSize=</c> with both path segments escaped, whose
/// bare JSON array maps onto <c>AuditHistoryEntryDTO</c>; a server answer that is not 2xx, and a
/// request that never completes, both come back as a failed <see cref="Result"/>, never an
/// exception; blank route parts are a programming error and throw.
/// </summary>
public sealed class AuditHistoryServiceTests : IDisposable
{
    private const string StoredAccessToken = "audit-access-token";

    private const string TwoEntries = """
        [
          { "id": "11111111-1111-1111-1111-111111111111", "entityType": "MMCA.Store.Catalog.Domain.Products.Product", "entityKey": "17", "propertyName": "Name", "oldValue": "Alpha", "newValue": "Beta", "operation": "Modified", "changedBy": 42, "changedOn": "2026-11-11T14:30:00Z", "correlationId": "trace-1" },
          { "id": "22222222-2222-2222-2222-222222222222", "entityType": "MMCA.Store.Catalog.Domain.Products.Product", "entityKey": "17", "propertyName": null, "oldValue": null, "newValue": null, "operation": "Added", "changedBy": null, "changedOn": "2026-11-10T08:00:00Z", "correlationId": null }
        ]
        """;

    private readonly Mock<ITokenStorageService> _tokenStorage = new();

    private StubHttpMessageHandler _handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NotFound);

    public AuditHistoryServiceTests() =>
        _tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(StoredAccessToken);

    public void Dispose() => _handler.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetPage_BuildsEscapedRouteWithPageQuery()
    {
        var sut = CreateSut(HttpStatusCode.OK, "[]");

        await sut.GetPageAsync("Admin/Catalog/AuditHistory/", "Order Line", "a/b c", 3, 25, Ct);

        _handler.CallCount.Should().Be(1, "one GET per page");
        _handler.LastRequest.Method.Should().Be(HttpMethod.Get);
        _handler.LastRequest.Uri!.PathAndQuery.Should().Be(
            "/Admin/Catalog/AuditHistory/Order%20Line/a%2Fb%20c?page=3&pageSize=25",
            "the prefix loses its trailing slash and the alias and key are each Uri.EscapeDataString'd");
        _handler.LastRequest.Authorization!.Scheme.Should().Be("Bearer");
        _handler.LastRequest.Authorization.Parameter.Should().Be(StoredAccessToken);
    }

    [Fact]
    public async Task GetPage_Success_ReturnsEntries()
    {
        var sut = CreateSut(HttpStatusCode.OK, TwoEntries);

        var result = await sut.GetPageAsync("Admin/Catalog/AuditHistory", "Product", "17", 1, 20, Ct);

        result.IsSuccess.Should().BeTrue();
        var entries = result.Value!;
        entries.Should().HaveCount(2);
        entries[0].Id.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"), "entries keep the order the server sent");
        entries[0].PropertyName.Should().Be("Name");
        entries[0].OldValue.Should().Be("Alpha");
        entries[0].NewValue.Should().Be("Beta");
        entries[0].Operation.Should().Be("Modified");
        entries[0].ChangedBy.Should().Be(42);
        entries[0].ChangedOn.Should().Be(new DateTime(2026, 11, 11, 14, 30, 0, DateTimeKind.Utc));
        entries[0].CorrelationId.Should().Be("trace-1");
        entries[1].ChangedBy.Should().BeNull();
        entries[1].PropertyName.Should().BeNull();
        entries[1].Operation.Should().Be("Added");
    }

    [Fact]
    public async Task GetPage_404_ReturnsNotFoundFailure()
    {
        var sut = CreateSut(HttpStatusCode.NotFound);

        var result = await sut.GetPageAsync("Admin/Catalog/AuditHistory", "Product", "17", 1, 20, Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task GetPage_403_ReturnsForbiddenFailure_WithoutThrowing()
    {
        var sut = CreateSut(HttpStatusCode.Forbidden);

        var act = () => sut.GetPageAsync("Admin/Catalog/AuditHistory", "Product", "17", 1, 20, Ct);

        var result = (await act.Should().NotThrowAsync()).Subject;
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Type.Should().Be(ErrorType.Forbidden);
    }

    [Fact]
    public async Task GetPage_TransientServerError_IsRetried()
    {
        // Rule 2: the GET runs through the shared RetryPolicy, so a 503 followed by a 200 succeeds.
        // The policy's first backoff is about two seconds; this test waits it out on purpose.
        var attempts = 0;
        _handler.Dispose();
        _handler = new StubHttpMessageHandler(_ => Interlocked.Increment(ref attempts) == 1
            ? StubHttpMessageHandler.CreateResponse(HttpStatusCode.ServiceUnavailable)
            : StubHttpMessageHandler.CreateResponse(HttpStatusCode.OK, TwoEntries));
        var sut = new AuditHistoryService(new StubHttpClientFactory(_handler), _tokenStorage.Object);

        var result = await sut.GetPageAsync("Admin/Catalog/AuditHistory", "Product", "17", 1, 20, Ct);

        result.IsSuccess.Should().BeTrue();
        _handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetPage_TransportFault_ReturnsTransportFailure()
    {
        // An IOException is a transport fault the retry policy does not handle, so no backoff runs.
        _handler.Dispose();
        _handler = new StubHttpMessageHandler(_ => throw new IOException("The connection was reset."));
        var sut = new AuditHistoryService(new StubHttpClientFactory(_handler), _tokenStorage.Object);

        var result = await sut.GetPageAsync("Admin/Catalog/AuditHistory", "Product", "17", 1, 20, Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Code.Should().Be(HttpResultExecutor.TransportErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetPage_BlankAlias_Throws(string alias)
    {
        var sut = CreateSut(HttpStatusCode.OK, "[]");

        var act = () => sut.GetPageAsync("Admin/Catalog/AuditHistory", alias, "17", 1, 20, Ct);

        await act.Should().ThrowAsync<ArgumentException>();
        _handler.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("", "17")]
    [InlineData("  ", "17")]
    [InlineData("Admin/Catalog/AuditHistory", "")]
    [InlineData("Admin/Catalog/AuditHistory", "  ")]
    public async Task GetPage_BlankPrefixOrKey_Throws(string routePrefix, string entityKey)
    {
        var sut = CreateSut(HttpStatusCode.OK, "[]");

        var act = () => sut.GetPageAsync(routePrefix, "Product", entityKey, 1, 20, Ct);

        await act.Should().ThrowAsync<ArgumentException>();
        _handler.CallCount.Should().Be(0);
    }

    private AuditHistoryService CreateSut(HttpStatusCode statusCode, string? json = null)
    {
        _handler.Dispose();
        _handler = StubHttpMessageHandler.RespondingWith(statusCode, json);
        return new AuditHistoryService(new StubHttpClientFactory(_handler), _tokenStorage.Object);
    }
}
