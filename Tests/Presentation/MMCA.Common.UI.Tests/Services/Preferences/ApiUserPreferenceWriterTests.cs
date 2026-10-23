using System.IdentityModel.Tokens.Jwt;
using System.Net;
using AwesomeAssertions;
using MMCA.Common.UI.Services.Auth.Tokens;
using MMCA.Common.UI.Services.Preferences;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Preferences;

/// <summary>
/// The preference write is best-effort: the caller never learns it failed, so a request that cannot
/// succeed is pure cost and still counts as a failed request in telemetry. These pin the two guards that
/// keep a signed-out or stale session from spending one 401 per theme/culture toggle (ADR-027/028).
/// </summary>
public sealed class ApiUserPreferenceWriterTests
{
    private static string Jwt(DateTime expires) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            notBefore: expires.AddMinutes(-60),
            expires: expires));

    private static string FreshJwt() => Jwt(DateTime.UtcNow.AddMinutes(30));

    private static string ExpiredJwt() => Jwt(DateTime.UtcNow.AddMinutes(-5));

    private static (ApiUserPreferenceWriter Writer, StubHttpMessageHandler Handler) CreateSut(
        string? token,
        HttpStatusCode status = HttpStatusCode.NoContent)
    {
        var handler = StubHttpMessageHandler.RespondingWith(status);
        var storage = new Mock<ITokenStorageService>();
        storage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(token);
        return (new ApiUserPreferenceWriter(new StubHttpClientFactory(handler), storage.Object), handler);
    }

    [Fact]
    public async Task SaveAsync_WithFreshToken_PutsThePreference()
    {
        var (writer, handler) = CreateSut(FreshJwt());

        await writer.SaveAsync(culture: "es", theme: null);

        handler.CallCount.Should().Be(1);
        handler.LastRequest.Method.Should().Be(HttpMethod.Put);
        handler.LastRequest.Uri!.AbsolutePath.Should().EndWith("auth/preferences");
    }

    [Fact]
    public async Task SaveAsync_WithFreshToken_SendsTheBearerItself_WhenTheClientHasNoAuthHandler()
    {
        // Defect A-03: in Blazor Server the "APIClient"'s AuthDelegatingHandler resolves in a separate DI
        // scope with an empty token store, so relying on it sends the PUT anonymous (401). The stub factory
        // here has no auth handler, which is that Server-mode shape: the writer must attach the token it read.
        var token = FreshJwt();
        var (writer, handler) = CreateSut(token);

        await writer.SaveAsync(culture: "es", theme: null, TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(1);
        handler.LastRequest.Method.Should().Be(HttpMethod.Put);
        handler.LastRequest.Authorization.Should().NotBeNull(
            "the writer holds the fresh token and must attach it, not depend on a handler in another DI scope");
        handler.LastRequest.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Authorization.Parameter.Should().Be(token);
    }

    [Fact]
    public async Task SaveAsync_WhenAnonymous_DoesNotCallTheApi()
    {
        var (writer, handler) = CreateSut(token: null);

        await writer.SaveAsync(culture: "es", theme: null);

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SaveAsync_WithExpiredToken_DoesNotSpendAGuaranteed401()
    {
        var (writer, handler) = CreateSut(ExpiredJwt());

        await writer.SaveAsync(culture: "es", theme: null);

        handler.CallCount.Should().Be(0, "an expired token cannot be accepted, so the request is pure cost");
    }

    [Fact]
    public async Task SaveAsync_WithUnreadableToken_DoesNotCallTheApi()
    {
        var (writer, handler) = CreateSut("not-a-jwt");

        await writer.SaveAsync(culture: "es", theme: null);

        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task SaveAsync_AfterA401_StopsRetryingWithTheSameToken()
    {
        // Unexpired but refused: a revoked session or rotated signing key. Expiry cannot predict this,
        // so only the response tells us, and every later toggle would repeat the same failed request.
        var (writer, handler) = CreateSut(FreshJwt(), HttpStatusCode.Unauthorized);

        await writer.SaveAsync(culture: "es", theme: null);
        await writer.SaveAsync(culture: null, theme: "dark");
        await writer.SaveAsync(culture: "en-US", theme: null);

        handler.CallCount.Should().Be(1, "three toggles on a rejected session must cost one 401, not three");
    }

    [Fact]
    public async Task SaveAsync_AfterA401_ResumesOnceTheUserSignsInAgain()
    {
        var rejected = FreshJwt();
        var handler = StubHttpMessageHandler.RespondingWith(HttpStatusCode.Unauthorized);
        var storage = new Mock<ITokenStorageService>();
        storage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(rejected);
        var writer = new ApiUserPreferenceWriter(new StubHttpClientFactory(handler), storage.Object);

        await writer.SaveAsync(culture: "es", theme: null);
        await writer.SaveAsync(culture: "es", theme: null);
        handler.CallCount.Should().Be(1);

        // A new sign-in yields a different token, which the writer must not treat as the rejected one.
        // The expiry has to differ: JWTs carry second-resolution timestamps and no other varying claim
        // here, so two tokens minted in the same second are byte-identical and ARE the same session.
        storage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(Jwt(DateTime.UtcNow.AddMinutes(45)));
        await writer.SaveAsync(culture: "es", theme: null);

        handler.CallCount.Should().Be(2, "a new token is a new session and deserves its own attempt");
    }

    [Fact]
    public async Task SaveAsync_AfterASuccess_KeepsWriting()
    {
        var (writer, handler) = CreateSut(FreshJwt());

        await writer.SaveAsync(culture: "es", theme: null);
        await writer.SaveAsync(culture: null, theme: "dark");

        handler.CallCount.Should().Be(2, "nothing was rejected, so nothing should be suppressed");
    }

    [Fact]
    public async Task SaveAsync_WhileAnEarlierSaveIsInFlight_SendsOnlyAfterItCompletes()
    {
        // A theme toggle followed within a second or two by a language switch: the switcher awaits its
        // own save and then force-reloads. Sent concurrently, the theme PUT was cut off by that reload
        // (WASM) or lost the User row's rowversion race to the culture PUT, so the next sign-in applied
        // the stale server theme. The culture save must wait for the theme save first.
        var ct = TestContext.Current.CancellationToken;
        using var handler = new GatedFirstRequestHandler();
        var writer = CreateWriter(handler);

        var themeSave = writer.SaveAsync(culture: null, theme: "dark", ct);
        await handler.FirstRequestArrived.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var cultureSave = writer.SaveAsync(culture: "es", theme: null, ct);

        // Long enough for an unserialized second PUT to reach the handler.
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        handler.Bodies.Should().HaveCount(1, "the culture PUT must not overtake the theme PUT still in flight");

        handler.ReleaseFirstRequest();
        await Task.WhenAll(themeSave, cultureSave).WaitAsync(TimeSpan.FromSeconds(5), ct);

        handler.Bodies.Should().HaveCount(2);
        handler.Bodies[0].Should().Contain("dark");
        handler.Bodies[1].Should().Contain("es");
    }

    [Fact]
    public async Task SaveAsync_WhenTheEarlierSaveHangs_SendsAfterTheBoundedWait()
    {
        // The wait is bounded: a hung earlier PUT must not hold the culture switch's reload forever.
        var ct = TestContext.Current.CancellationToken;
        using var handler = new GatedFirstRequestHandler();
        var writer = CreateWriter(handler, previousSaveWait: TimeSpan.FromMilliseconds(100));

        var hungSave = writer.SaveAsync(culture: null, theme: "dark", ct);
        await handler.FirstRequestArrived.WaitAsync(TimeSpan.FromSeconds(5), ct);

        await writer.SaveAsync(culture: "es", theme: null, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        handler.Bodies.Should().HaveCount(2);
        handler.Bodies[1].Should().Contain("es");

        handler.ReleaseFirstRequest();
        await hungSave.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    private static ApiUserPreferenceWriter CreateWriter(HttpMessageHandler handler, TimeSpan? previousSaveWait = null)
    {
        var storage = new Mock<ITokenStorageService>();
        storage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync(FreshJwt());
        return new ApiUserPreferenceWriter(new StubHttpClientFactory(handler), storage.Object)
        {
            PreviousSaveWait = previousSaveWait ?? TimeSpan.FromSeconds(5),
        };
    }

    /// <summary>Holds the first request until released; answers every later one at once.</summary>
    private sealed class GatedFirstRequestHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _bodies = [];

        public Task FirstRequestArrived => _firstArrived.Task;

        public IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_bodies)
                {
                    return [.. _bodies];
                }
            }
        }

        public void ReleaseFirstRequest() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            bool isFirst;
            lock (_bodies)
            {
                _bodies.Add(body);
                isFirst = _bodies.Count == 1;
            }

            if (isFirst)
            {
                _firstArrived.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
