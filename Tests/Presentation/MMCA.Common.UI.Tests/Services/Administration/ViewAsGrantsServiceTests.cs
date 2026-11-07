using System.Net;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Administration;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Administration;

/// <summary>
/// Pins <see cref="ViewAsGrantsService.GetPageAsync"/>: a bearer-authenticated
/// <c>GET Admin/ViewAsGrants</c> carrying the page and only the filters that are set, whose camelCase
/// payload maps onto <see cref="ViewAsGrantSummaryResponse"/>, and a 404 that comes back as a failed
/// <see cref="Result"/>, never an exception.
/// </summary>
public sealed class ViewAsGrantsServiceTests : IDisposable
{
    private const string StoredAccessToken = "stored-access-token";

    private const string PageBody = """{ "items": [ { "id": "22222222-2222-2222-2222-222222222222", "staffUserId": 1, "staffDisplay": "ana@staff.test", "targetUserId": 10, "targetDisplay": null, "reason": "Ticket 4411: schedule missing", "grantedAt": "2026-11-07T11:30:00Z", "expiresAt": "2026-11-07T11:45:00Z", "endedAt": null, "status": "Expired" } ], "paginationMetadata": { "totalItemCount": 21, "pageSize": 20, "currentPage": 2 } }""";

    private readonly Mock<ITokenStorageService> _tokenStorage = new();

    private StubHttpMessageHandler _handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NotFound);

    public ViewAsGrantsServiceTests() =>
        _tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(StoredAccessToken);

    public void Dispose() => _handler.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetPageAsync_SendsThePageAndOnlyTheFiltersThatAreSet_WithTheBearerToken()
    {
        var sut = CreateSut(HttpStatusCode.OK, PageBody);

        await sut.GetPageAsync(
            new ViewAsGrantListRequest
            {
                Staff = " ana ",
                From = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                Status = ViewAsGrantStatus.Live,
                PageNumber = 2,
            },
            Ct);

        _handler.LastRequest.Method.Should().Be(HttpMethod.Get);
        _handler.LastRequest.Uri!.AbsolutePath.Should().Be("/Admin/ViewAsGrants");
        _handler.LastRequest.Uri.Query.Should().Be("?pageNumber=2&pageSize=20&staff=ana&from=2026-11-01T00%3A00%3A00.0000000Z&status=Live");
        _handler.LastRequest.Authorization!.Parameter.Should().Be(StoredAccessToken);
    }

    [Fact]
    public async Task GetPageAsync_MapsTheCamelCasePayload()
    {
        var sut = CreateSut(HttpStatusCode.OK, PageBody);

        var result = await sut.GetPageAsync(new ViewAsGrantListRequest(), Ct);

        var page = result.Value!;
        page.PaginationMetadata.TotalItemCount.Should().Be(21);
        var row = page.Items.Should().ContainSingle().Subject;
        row.StaffDisplay.Should().Be("ana@staff.test");
        row.TargetDisplay.Should().BeNull();
        row.Status.Should().Be(ViewAsGrantStatus.Expired);
        row.GrantedAt.Should().Be(new DateTime(2026, 11, 7, 11, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task GetPageAsync_On404_ReportsANotFoundFailure()
    {
        var sut = CreateSut(HttpStatusCode.NotFound);

        var result = await sut.GetPageAsync(new ViewAsGrantListRequest(), Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Type.Should().Be(ErrorType.NotFound);
    }

    private ViewAsGrantsService CreateSut(HttpStatusCode statusCode, string? json = null)
    {
        _handler.Dispose();
        _handler = StubHttpMessageHandler.RespondingWith(statusCode, json);
        return new ViewAsGrantsService(new StubHttpClientFactory(_handler), _tokenStorage.Object);
    }
}
