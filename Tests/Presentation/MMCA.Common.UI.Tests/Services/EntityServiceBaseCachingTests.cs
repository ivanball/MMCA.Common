using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services;
using MMCA.Common.UI.Services.Auth;
using MMCA.Common.UI.Services.Caching;
using MMCA.Common.UI.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.UI.Tests.Services;

/// <summary>
/// The read-through half of <see cref="EntityServiceBase{TEntityDTO, TId}"/> (§19): with an
/// <see cref="IUiReadCache"/> injected, repeated reads inside the TTL collapse onto one HTTP call,
/// a failed read is never cached, an explicit bypass forces a round trip, and a successful write
/// invalidates the endpoint so the next read is authoritative again. With no cache injected the
/// service behaves exactly as <c>EntityServiceBaseTests</c> pins it.
/// </summary>
public sealed class EntityServiceBaseCachingTests
{
    private sealed record WidgetDto : IBaseDTO<int>
    {
        public required int Id { get; init; }

        public string? Name { get; init; }
    }

    /// <summary>
    /// Minimal concrete service. <see cref="GetAllBypassingCacheAsync"/> is the derived-service view of
    /// the bypass switch: a page with a refresh button calls a method shaped like this rather than
    /// getting a fresh-by-the-clock entry it did not ask for.
    /// </summary>
    private sealed class WidgetService(
        IHttpClientFactory httpClientFactory,
        ITokenStorageService tokenStorageService,
        IUiReadCache? readCache)
        : EntityServiceBase<WidgetDto, int>("widgets", httpClientFactory, tokenStorageService, readCache)
    {
        public async Task<Result<PagedCollectionResult<WidgetDto>>> GetAllBypassingCacheAsync(CancellationToken cancellationToken) =>
            await GetCachedAsync<PagedCollectionResult<WidgetDto>>(
                "widgets?includeFKs=False&includeChildren=False", cancellationToken, bypassCache: true);
    }

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private static WidgetDto Widget(int id, string name = "Widget") => new() { Id = id, Name = name };

    private static HttpResponseMessage PagedResponse(params WidgetDto[] items) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                new PagedCollectionResult<WidgetDto>(items, new PaginationMetadata(items.Length, 25, 1))),
        };

    private (WidgetService Sut, StubHttpMessageHandler Handler) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        IUiReadCache? readCache)
    {
        var handler = new StubHttpMessageHandler(responder);
        var tokenStorage = new Mock<ITokenStorageService>();
        tokenStorage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync("stored-access-token");
        return (new WidgetService(new StubHttpClientFactory(handler), tokenStorage.Object, readCache), handler);
    }

    private UiReadCache CreateCache(TimeSpan? defaultTtl = null) =>
        new(_clock, Options.Create(new UiReadCacheOptions { DefaultTtl = defaultTtl ?? TimeSpan.FromSeconds(60) }));

    /// <summary>Runs one CRUD write by name, flattening the create's typed result to a bare outcome.</summary>
    private static Task<Result> WriteAsync(WidgetService service, string verb, CancellationToken cancellationToken) =>
        verb switch
        {
            "add" => Flatten(service.AddAsync(Widget(0), cancellationToken)),
            "update" => service.UpdateAsync(Widget(1, "Renamed"), cancellationToken),
            _ => service.DeleteAsync(1, cancellationToken),
        };

    private static async Task<Result> Flatten<T>(Task<Result<T>> pending) => await pending;

    /// <summary>
    /// The shared responder: each read shape answers with its own payload so a hit served under the
    /// wrong key would deserialize into the wrong thing, and every write answers 2xx.
    /// </summary>
    private static HttpResponseMessage RespondByShape(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get)
        {
            return request.Method == HttpMethod.Post
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Widget(2)) }
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        var path = request.RequestUri!.PathAndQuery;

        if (path.Contains("/lookup", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new CollectionResult<BaseLookup<int>>(
                    [new BaseLookup<int> { Id = 1, Name = "First" }])),
            };
        }

        return path.Contains("/widgets/7", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Widget(7)) }
            : PagedResponse(Widget(1));
    }

    // == Read-through ==
    [Fact]
    public async Task TwoIdenticalReadsInsideTheTtl_HitTheApiOnce()
    {
        var (sut, handler) = CreateSut(_ => PagedResponse(Widget(1)), CreateCache());

        var first = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var second = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(1, "the second read is inside the freshness budget");
        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value!.Select(w => w.Id).Should().Equal(1);
    }

    [Fact]
    public async Task AReadPastTheTtl_GoesBackToTheApi()
    {
        var (sut, handler) = CreateSut(_ => PagedResponse(Widget(1)), CreateCache());

        await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task ReadsThatDifferByOneQueryParameter_AreTwoCallsAndTwoEntries()
    {
        // The cache key is the path plus the FULL query string, mirroring the server's
        // QueryKeys = "*" rule (ADR-040): a paging or filter change must not be answered from the
        // entry belonging to a different page.
        var (sut, handler) = CreateSut(_ => PagedResponse(Widget(1)), CreateCache());

        await sut.GetPagedAsync([], pageNumber: 1, pageSize: 25, sortColumn: null, sortDirection: null,
            includeChildren: false, TestContext.Current.CancellationToken);
        await sut.GetPagedAsync([], pageNumber: 2, pageSize: 25, sortColumn: null, sortDirection: null,
            includeChildren: false, TestContext.Current.CancellationToken);
        await sut.GetPagedAsync([], pageNumber: 1, pageSize: 25, sortColumn: null, sortDirection: null,
            includeChildren: false, TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(2, "page 1 is served from its own entry the second time");
    }

    [Fact]
    public async Task TheFourReadMethods_EachCacheUnderTheirOwnUrl()
    {
        var (sut, handler) = CreateSut(RespondByShape, CreateCache());

        var token = TestContext.Current.CancellationToken;
        for (var pass = 0; pass < 2; pass++)
        {
            await sut.GetAllAsync(cancellationToken: token);
            await sut.GetPagedAsync([], 1, 25, null, null, false, token);
            await sut.GetAllForLookupAsync("Name", token);
            await sut.GetByIdAsync(7, cancellationToken: token);
        }

        handler.CallCount.Should().Be(4, "the second pass is served entirely from cache");
    }

    // == Bypass ==
    [Fact]
    public async Task BypassCache_ForcesASecondCallAndLeavesTheEntryAlone()
    {
        var (sut, handler) = CreateSut(_ => PagedResponse(Widget(1)), CreateCache());

        await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);
        await sut.GetAllBypassingCacheAsync(TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(2);

        // The bypass neither read nor rewrote the entry, so the cached answer is still serving.
        await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);
        handler.CallCount.Should().Be(2);
    }

    // == Failures are never cached ==
    [Fact]
    public async Task AFailedRead_IsNotCached()
    {
        // Caching a failure would pin the error in front of the user for the whole TTL. A 4xx rather
        // than a 5xx so the class-level Polly retry stays out of the way (2s/4s/8s of backoff).
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.BadRequest),
            PagedResponse(Widget(1)),
        ]);
        var (sut, handler) = CreateSut(_ => responses.Dequeue(), CreateCache());

        var failed = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);
        var recovered = await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        failed.IsFailure.Should().BeTrue();
        recovered.IsSuccess.Should().BeTrue();
        handler.CallCount.Should().Be(2, "the failure left nothing behind to serve");
    }

    [Fact]
    public async Task ANotFoundById_IsNotCached()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.NotFound),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Widget(7)) },
        ]);
        var (sut, handler) = CreateSut(_ => responses.Dequeue(), CreateCache());

        var missing = await sut.GetByIdAsync(7, cancellationToken: TestContext.Current.CancellationToken);
        var found = await sut.GetByIdAsync(7, cancellationToken: TestContext.Current.CancellationToken);

        missing.IsFailure.Should().BeTrue();
        found.IsSuccess.Should().BeTrue();
        handler.CallCount.Should().Be(2);
    }

    // == Write invalidation ==
    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task ASuccessfulWrite_InvalidatesTheEndpointSoTheNextReadRefetches(string verb)
    {
        var (sut, handler) = CreateSut(RespondByShape, CreateCache());
        var token = TestContext.Current.CancellationToken;

        await sut.GetAllAsync(cancellationToken: token);

        var write = await WriteAsync(sut, verb, token);
        await sut.GetAllAsync(cancellationToken: token);

        write.IsSuccess.Should().BeTrue();
        handler.CallCount.Should().Be(3, "the write cleared the list the read had cached");
    }

    [Fact]
    public async Task AWriteThatFailed_LeavesTheCacheAlone()
    {
        // A rejected write changed nothing on the server, so the cached reads are still accurate;
        // invalidating there would throw away entries for no reason.
        var (sut, handler) = CreateSut(
            request => request.Method == HttpMethod.Get
                ? PagedResponse(Widget(1))
                : new HttpResponseMessage(HttpStatusCode.Conflict),
            CreateCache());
        var token = TestContext.Current.CancellationToken;

        await sut.GetAllAsync(cancellationToken: token);
        var rejected = await sut.UpdateAsync(Widget(1, "Renamed"), token);
        await sut.GetAllAsync(cancellationToken: token);

        rejected.IsFailure.Should().BeTrue();
        handler.CallCount.Should().Be(2, "only the read and the rejected write reached the wire");
    }

    [Fact]
    public async Task AWrite_InvalidatesEveryShapeOfReadOnTheEndpoint()
    {
        var (sut, handler) = CreateSut(RespondByShape, CreateCache());
        var token = TestContext.Current.CancellationToken;

        await sut.GetAllAsync(cancellationToken: token);
        await sut.GetPagedAsync([], 1, 25, null, null, false, token);
        await sut.GetByIdAsync(7, cancellationToken: token);
        handler.CallCount.Should().Be(3);

        await sut.DeleteAsync(1, token);

        await sut.GetAllAsync(cancellationToken: token);
        await sut.GetPagedAsync([], 1, 25, null, null, false, token);
        await sut.GetByIdAsync(7, cancellationToken: token);

        handler.CallCount.Should().Be(7, "list, paged and by-id all share the endpoint prefix the write cleared");
    }

    // == No cache registered ==
    [Fact]
    public async Task WithNoCacheRegistered_EveryReadStillReachesTheApi()
    {
        var (sut, handler) = CreateSut(_ => PagedResponse(Widget(1)), readCache: null);

        await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);
        await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task WithADisabledCache_EveryReadStillReachesTheApi()
    {
        // The configuration switch has the same effect as registering no cache at all.
        var disabled = new UiReadCache(_clock, Options.Create(new UiReadCacheOptions { Enabled = false }));
        var (sut, handler) = CreateSut(_ => PagedResponse(Widget(1)), disabled);

        await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);
        await sut.GetAllAsync(cancellationToken: TestContext.Current.CancellationToken);

        handler.CallCount.Should().Be(2);
    }
}
