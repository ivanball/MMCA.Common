using System.Net;
using System.Text;
using AwesomeAssertions;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Http;
using MMCA.Common.Testing.UI;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Tests.Services.Api;

/// <summary>
/// Pins the write-safety half of <see cref="EntityServiceBase{TEntityDTO, TId}"/>: the
/// <c>Idempotency-Key</c> emitted on creates and updates (never on reads or deletes), the key
/// staying identical across every retry attempt of one logical operation (with the update's
/// <c>If-Match</c> beside it), and the shape of the retry predicate
/// (5xx yes, 501 no, 429 yes) plus cancellation aborting the pipeline. The retried cases pay the
/// real Polly backoff (2s, then 4s), so each one is driven through the smallest attempt count that
/// proves the behavior and asserts on the handler's captured attempts, never on wall-clock timing.
/// </summary>
public sealed class EntityServiceBaseIdempotencyRetryTests
{
    private const string WidgetJson = """{"id":7,"name":"Blue"}""";

    /// <summary>An empty page, so a read under test succeeds instead of failing on an absent body.</summary>
    private const string EmptyPageJson =
        """{"items":[],"paginationMetadata":{"totalItemCount":0,"pageSize":10,"currentPage":1}}""";

    private sealed record WidgetDto : IBaseDTO<int>
    {
        public required int Id { get; init; }

        public string? Name { get; init; }
    }

    private sealed class WidgetService(IHttpClientFactory httpClientFactory, ITokenStorageService tokenStorageService)
        : EntityServiceBase<WidgetDto, int>("widgets", httpClientFactory, tokenStorageService);

    /// <summary>A DTO carrying a concurrency token, so its update sends <c>If-Match</c>.</summary>
    private sealed record VersionedWidgetDto : IBaseDTO<int>, IConcurrencyAware
    {
        public required int Id { get; init; }

        public string? Name { get; init; }

        public byte[] RowVersion { get; init; } = [];
    }

    private sealed class VersionedWidgetService(IHttpClientFactory httpClientFactory, ITokenStorageService tokenStorageService)
        : EntityServiceBase<VersionedWidgetDto, int>("widgets", httpClientFactory, tokenStorageService);

    /// <summary>
    /// Records the <c>Idempotency-Key</c> and <c>If-Match</c> headers of every attempt (null when
    /// absent) and answers with a scripted status sequence, where a null status throws a transport
    /// failure (the response never arrived); the last scripted status repeats if the policy asks for
    /// more attempts than the script supplies. A fresh response is built per attempt so a retry
    /// never reuses a consumed <see cref="HttpContent"/>. The local double exists because the
    /// shared capturing handlers record only the Authorization header.
    /// </summary>
    private sealed class ScriptedHandler(string? json, params HttpStatusCode?[] statuses) : HttpMessageHandler
    {
        private int _attempt;

        public List<string?> CapturedKeys { get; } = [];

        public List<string?> CapturedIfMatch { get; } = [];

        public int AttemptCount => CapturedKeys.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedKeys.Add(
                request.Headers.TryGetValues(IdempotencyHeaders.IdempotencyKey, out var values)
                    ? string.Join(",", values)
                    : null);
            CapturedIfMatch.Add(
                request.Headers.TryGetValues(ConcurrencyETag.IfMatchHeaderName, out var tags)
                    ? string.Join(",", tags)
                    : null);

            var status = statuses[Math.Min(_attempt, statuses.Length - 1)];
            _attempt++;

            if (status is null)
            {
                throw new HttpRequestException("The connection dropped before the response arrived.");
            }

            var response = new HttpResponseMessage(status.Value);
            if (json is not null)
            {
                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }

    private static (WidgetService Sut, ScriptedHandler Handler) CreateSut(string? json, params HttpStatusCode?[] statuses)
    {
        var handler = new ScriptedHandler(json, statuses);
        var factory = new FreshApiClientFactory(handler, new Uri("http://localhost/"));
        return (new WidgetService(factory, new StubTokenStorageService()), handler);
    }

    private static WidgetDto NewWidget() => new() { Id = 0, Name = "Blue" };

    // == Idempotency key on creates ==
    [Fact]
    public async Task AddAsync_SendsAnIdempotencyKeyHeader()
    {
        var (sut, handler) = CreateSut(WidgetJson, HttpStatusCode.OK);

        await sut.AddAsync(NewWidget(), TestContext.Current.CancellationToken);

        handler.AttemptCount.Should().Be(1);
        handler.CapturedKeys[0].Should().MatchRegex("^[0-9a-f]{32}$", "the key is a compact-form GUID");
    }

    [Fact]
    public async Task AddAsync_WhenAttemptsFail_ReusesTheSameKeyOnEveryRetry()
    {
        var (sut, handler) = CreateSut(
            WidgetJson,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.OK);

        var result = await sut.AddAsync(NewWidget(), TestContext.Current.CancellationToken);

        var firstKey = handler.CapturedKeys[0];
        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(7);
        handler.AttemptCount.Should().Be(3);
        firstKey.Should().MatchRegex("^[0-9a-f]{32}$");
        handler.CapturedKeys.Should().OnlyContain(
            capturedKey => capturedKey == firstKey,
            "a fresh key per attempt would let a retried create insert a second record");
    }

    // == No key on reads and naturally idempotent writes ==
    [Fact]
    public async Task GetAllAsync_SendsNoIdempotencyKey()
    {
        var (sut, handler) = CreateSut(EmptyPageJson, HttpStatusCode.OK);

        var result = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        handler.AttemptCount.Should().Be(1);
        handler.CapturedKeys[0].Should().BeNull("a read changes nothing, so it needs no dedup key");
    }

    // == Idempotency key on updates (C1) ==
    [Fact]
    public async Task UpdateAsync_SendsAnIdempotencyKeyHeader()
    {
        var (sut, handler) = CreateSut(null, HttpStatusCode.NoContent);

        var result = await sut.UpdateAsync(
            new WidgetDto { Id = 7, Name = "Renamed" }, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        handler.AttemptCount.Should().Be(1);
        handler.CapturedKeys[0].Should().MatchRegex(
            "^[0-9a-f]{32}$",
            "a PUT whose response is lost must be replayable by the server instead of re-checked against the version it wrote");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(null)]
    public async Task UpdateAsync_WhenTheFirstAttemptFails_ReusesTheSameKeyAndIfMatchOnEveryRetry(HttpStatusCode? firstAttempt)
    {
        var handler = new ScriptedHandler(null, firstAttempt, HttpStatusCode.NoContent);
        var sut = new VersionedWidgetService(
            new FreshApiClientFactory(handler, new Uri("http://localhost/")), new StubTokenStorageService());
        var widget = new VersionedWidgetDto { Id = 7, Name = "Renamed", RowVersion = [1, 2, 3, 4, 5, 6, 7, 8] };

        var result = await sut.UpdateAsync(widget, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue("the retry after a 5xx or a dropped connection lands");
        handler.AttemptCount.Should().Be(2);
        var firstKey = handler.CapturedKeys[0];
        firstKey.Should().MatchRegex("^[0-9a-f]{32}$");
        handler.CapturedKeys.Should().OnlyContain(
            capturedKey => capturedKey == firstKey,
            "the key is minted once per update, so the server can replay an attempt that landed and lost its response");
        var expectedTag = ConcurrencyETag.Format(widget.RowVersion);
        handler.CapturedIfMatch.Should().OnlyContain(
            tag => tag == expectedTag,
            "every attempt states the same precondition as the first");
    }

    [Fact]
    public async Task DeleteAsync_SendsNoIdempotencyKey()
    {
        var (sut, handler) = CreateSut(null, HttpStatusCode.NoContent);

        var result = await sut.DeleteAsync(7, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        handler.AttemptCount.Should().Be(1);
        handler.CapturedKeys[0].Should().BeNull("a repeated DELETE of the same id is already idempotent");
    }

    // == Retry predicate ==
    [Fact]
    public async Task NotImplementedResponse_IsNotRetried()
    {
        var (sut, handler) = CreateSut(null, HttpStatusCode.NotImplemented);

        var result = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be("Http.501");
        error.Type.Should().Be(ErrorType.Unexpected);
        handler.AttemptCount.Should().Be(
            1, "501 is a permanent verdict, so retrying only burns the budget and delays the error");
    }

    [Fact]
    public async Task TooManyRequestsResponse_IsRetried()
    {
        var (sut, handler) = CreateSut(EmptyPageJson, HttpStatusCode.TooManyRequests, HttpStatusCode.OK);

        var result = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        handler.AttemptCount.Should().Be(2, "429 is the server explicitly inviting a later attempt");
    }

    // == Cancellation ==
    [Fact]
    public async Task AlreadyCancelledToken_AbortsWithoutExhaustingTheRetries()
    {
        var (sut, handler) = CreateSut(WidgetJson, HttpStatusCode.InternalServerError);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => sut.AddAsync(NewWidget(), cts.Token);

        // The caller's own cancellation is the one outcome that is still an exception rather than a
        // failed Result: the page asked for the operation to stop, so it has nothing to render.
        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.AttemptCount.Should().Be(
            0, "the token reaches the policy, so an abandoned operation never sleeps out its backoff budget");
    }
}
