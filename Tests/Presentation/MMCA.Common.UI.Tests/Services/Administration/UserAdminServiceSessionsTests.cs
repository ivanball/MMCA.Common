using System.Net;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Services.Administration;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Administration;

/// <summary>
/// Pins <see cref="UserAdminService{TUserDto}.GetSessionsAsync"/>: a bearer-authenticated
/// <c>GET Admin/Users/{userId}/sessions</c> whose camelCase payload maps onto
/// <see cref="MMCA.Common.Shared.Auth.Responses.RefreshSessionSummaryResponse"/>, and a refusal that
/// comes back as a failed <see cref="Result"/> with the API's own error type, never an exception.
/// </summary>
public sealed class UserAdminServiceSessionsTests : IDisposable
{
    private const string StoredAccessToken = "stored-access-token";

    /// <summary>Exactly the camelCase wire shape the admin sessions endpoint emits, newest first.</summary>
    private const string SessionsBody = """[ { "sessionId": "22222222-2222-2222-2222-222222222222", "createdAt": "2026-08-02T11:00:00Z", "expiresAt": "2026-09-02T11:00:00Z", "ipAddress": null, "userAgent": "AtlDevCon/1.9.2 (Android 15; MmcaApp)", "isCurrent": false }, { "sessionId": "11111111-1111-1111-1111-111111111111", "createdAt": "2026-08-01T09:30:00Z", "expiresAt": "2026-09-01T09:30:00Z", "ipAddress": "203.0.113.7", "userAgent": "Mozilla/5.0 (Windows NT 10.0) Chrome/126.0.0.0", "isCurrent": false } ]""";

    private const string ForbiddenBody = """{ "title": "Forbidden", "status": 403, "errors": [ { "code": "Auth.Forbidden", "message": "Not allowed.", "type": "Forbidden" } ] }""";

    private readonly Mock<ITokenStorageService> _tokenStorage = new();

    private StubHttpMessageHandler _handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.NotFound);

    public UserAdminServiceSessionsTests() =>
        _tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(StoredAccessToken);

    public void Dispose() => _handler.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UserAdminService<TestUserDto> CreateSut(HttpStatusCode statusCode, string? json = null)
    {
        _handler.Dispose();
        _handler = StubHttpMessageHandler.RespondingWith(statusCode, json);
        return new UserAdminService<TestUserDto>(new StubHttpClientFactory(_handler), _tokenStorage.Object);
    }

    [Fact]
    public async Task GetSessionsAsync_GetsTheUsersSessionsEndpointWithTheBearerToken()
    {
        var sut = CreateSut(HttpStatusCode.OK, SessionsBody);

        await sut.GetSessionsAsync(42, Ct);

        _handler.CallCount.Should().Be(1);
        _handler.LastRequest.Method.Should().Be(HttpMethod.Get);
        _handler.LastRequest.Uri!.AbsolutePath.Should().Be("/Admin/Users/42/sessions");
        _handler.LastRequest.Authorization!.Parameter.Should().Be(StoredAccessToken);
    }

    [Fact]
    public async Task GetSessionsAsync_MapsTheCamelCasePayloadInOrder()
    {
        var sut = CreateSut(HttpStatusCode.OK, SessionsBody);

        var result = await sut.GetSessionsAsync(42, Ct);

        result.IsSuccess.Should().BeTrue();
        var sessions = result.Value!;
        sessions.Should().HaveCount(2);
        sessions[0].SessionId.Should().Be(new Guid(0x22222222, 0x2222, 0x2222, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22));
        sessions[0].CreatedAt.Should().Be(new DateTime(2026, 8, 2, 11, 0, 0, DateTimeKind.Utc));
        sessions[0].ExpiresAt.Should().Be(new DateTime(2026, 9, 2, 11, 0, 0, DateTimeKind.Utc));
        sessions[0].IpAddress.Should().BeNull();
        sessions[0].UserAgent.Should().Be("AtlDevCon/1.9.2 (Android 15; MmcaApp)");
        sessions[0].IsCurrent.Should().BeFalse();
        sessions[1].SessionId.Should().Be(new Guid(0x11111111, 0x1111, 0x1111, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11));
        sessions[1].IpAddress.Should().Be("203.0.113.7");
        sessions[1].UserAgent.Should().Contain("Chrome/126.0.0.0");
    }

    [Fact]
    public async Task GetSessionsAsync_WithNoLiveSessions_SucceedsWithAnEmptyList()
    {
        var sut = CreateSut(HttpStatusCode.OK, "[]");

        var result = await sut.GetSessionsAsync(42, Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSessionsAsync_When403_ReportsTheApisForbiddenFailure()
    {
        var sut = CreateSut(HttpStatusCode.Forbidden, ForbiddenBody);

        var result = await sut.GetSessionsAsync(42, Ct);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle().Subject.Type.Should().Be(ErrorType.Forbidden);
    }

    /// <summary>Stand-in for an app's administration user DTO; the sessions call never reads it.</summary>
    public sealed record TestUserDto(int Id, string Email);
}
