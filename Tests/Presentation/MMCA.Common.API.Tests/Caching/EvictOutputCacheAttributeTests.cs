using System.Net;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Caching;
using MMCA.Common.API.Concurrency;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.API.Tests.Caching;

/// <summary>
/// The declarative eviction result filter: a successful write evicts its output-cache tags before the
/// response body goes out, a read or a failed write evicts nothing, a cache outage never fails the
/// request, and one tag is evicted once per request however many instances name it.
/// </summary>
public sealed class EvictOutputCacheAttributeTests
{
    private const string TagA = "probe:a";
    private const string TagB = "probe:b";

    // -- Rule 1: constructor guards --
    [Fact]
    public void Ctor_NullTags_Throws()
    {
        var act = () => new EvictOutputCacheAttribute(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_EmptyTags_Throws()
    {
        var act = () => new EvictOutputCacheAttribute();

        act.Should().Throw<ArgumentException>("an eviction filter with nothing to evict is a wiring mistake");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_BlankTag_Throws(string? blank)
    {
        var act = () => new EvictOutputCacheAttribute(TagA, blank!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Tags_ReturnsTheConstructorTagsInOrder() =>
        new EvictOutputCacheAttribute(TagA, TagB).Tags.Should().Equal(TagA, TagB);

    // -- Rule 2: reads never evict --
    [Fact]
    public async Task Get_NeverEvicts()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var context = CreateContext(HttpMethods.Get, new OkObjectResult("row"), services);

        var nextCalled = await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        nextCalled.Should().BeTrue();
        store.Evictions.Should().BeEmpty("a read changes nothing a cached read could have served");
    }

    [Fact]
    public async Task Head_NeverEvicts()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var context = CreateContext(HttpMethods.Head, new OkResult(), services);

        var nextCalled = await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        nextCalled.Should().BeTrue();
        store.Evictions.Should().BeEmpty();
    }

    // -- Rules 3 and 4: a 2xx evicts every tag, before next() --
    [Fact]
    public async Task Post_204_EvictsEveryTag_BeforeNext()
    {
        var journal = new List<string>();
        var store = new RecordingOutputCacheStore(journal);
        await using var services = BuildServices(store);
        var context = CreateContext(HttpMethods.Post, new NoContentResult(), services);

        await RunAsync(new EvictOutputCacheAttribute(TagA, TagB), context, journal);

        journal.Should().Equal(
            [$"evict:{TagA}", $"evict:{TagB}", "next"],
            "eviction precedes the response body, exactly as the hand-written calls did");
    }

    [Fact]
    public async Task Put_200ObjectResult_Evicts()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var context = CreateContext(HttpMethods.Put, new OkObjectResult(new { Id = 1 }), services);

        await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        store.EvictedTags.Should().Equal(TagA);
    }

    [Fact]
    public async Task Post_201CreatedAtRoute_Evicts()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var context = CreateContext(
            HttpMethods.Post,
            new CreatedAtRouteResult("GetProbeById", new { id = 1 }, new { Id = 1 }),
            services);

        await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        store.EvictedTags.Should().Equal(TagA);
    }

    [Fact]
    public async Task Delete_204_Evicts()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var context = CreateContext(HttpMethods.Delete, new NoContentResult(), services);

        await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        store.EvictedTags.Should().Equal(TagA);
    }

    // -- Rules 3 and 5: a non-2xx evicts nothing; the result's status wins over the response's --
    [Fact]
    public async Task Put_400ProblemDetails_DoesNotEvict()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var problem = new ObjectResult(new ProblemDetails { Status = StatusCodes.Status400BadRequest })
        {
            StatusCode = StatusCodes.Status400BadRequest,
        };

        // The response still reads 200 at this stage; the result's own status is the one that counts.
        var context = CreateContext(HttpMethods.Put, problem, services, responseStatusCode: StatusCodes.Status200OK);

        var nextCalled = await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        nextCalled.Should().BeTrue();
        store.Evictions.Should().BeEmpty("a rejected write changed nothing");
    }

    [Fact]
    public async Task Put_404_DoesNotEvict()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var context = CreateContext(HttpMethods.Put, new NotFoundResult(), services);

        await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        store.Evictions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(StatusCodes.Status200OK, true)]
    [InlineData(StatusCodes.Status500InternalServerError, false)]
    public async Task ResultWithoutStatusCode_UsesResponseStatusCode(int responseStatusCode, bool expectEviction)
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);

        // EmptyResult carries no status code, so the response's status is the final one.
        var context = CreateContext(HttpMethods.Put, new EmptyResult(), services, responseStatusCode);

        await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        store.Evictions.Should().HaveCount(expectEviction ? 1 : 0);
    }

    [Fact]
    public async Task ObjectResultWithNullStatusCode_UsesResponseStatusCode()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var context = CreateContext(
            HttpMethods.Put,
            new ObjectResult("body") { StatusCode = null },
            services,
            responseStatusCode: StatusCodes.Status409Conflict);

        await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        store.Evictions.Should().BeEmpty("a null result status falls back to the response status, here a 409");
    }

    // -- Rule 6: best effort, under CancellationToken.None --
    [Fact]
    public async Task StoreThrows_RequestStillSucceeds_AndWarningLogged()
    {
        var store = new RecordingOutputCacheStore { FailingTag = TagA };
        var logger = new RecordingLogger<EvictOutputCacheAttribute>();
        await using var services = BuildServices(store, logger);
        var context = CreateContext(HttpMethods.Put, new NoContentResult(), services);

        var act = async () => await RunAsync(new EvictOutputCacheAttribute(TagA, TagB), context);

        (await act.Should().NotThrowAsync("the write has committed; a cache outage must not fail it"))
            .Which.Should().BeTrue("the response still goes out");
        store.EvictedTags.Should().Contain(TagB, "a failure on one tag does not skip the rest");
        logger.Levels.Should().Contain(LogLevel.Warning, "the failed eviction is reported through the request's logger");
    }

    [Fact]
    public async Task Eviction_RunsUnderCancellationTokenNone()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var context = CreateContext(HttpMethods.Put, new NoContentResult(), services);
        context.HttpContext.RequestAborted = aborted.Token;

        await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        store.Evictions.Should().ContainSingle().Which.Token.CanBeCanceled.Should().BeFalse(
            "a client that disconnected mid-response must not abandon the cleanup");
    }

    // -- Rules 7 and 8: missing services --
    [Fact]
    public async Task NoOutputCacheStoreRegistered_DoesNotThrow()
    {
        await using var services = BuildServices(store: null);
        var context = CreateContext(HttpMethods.Put, new NoContentResult(), services);

        var act = async () => await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        (await act.Should().NotThrowAsync("a host without AddOutputCache has nothing to evict"))
            .Which.Should().BeTrue();
    }

    [Fact]
    public async Task NoLoggerRegistered_StoreThrows_DoesNotThrow()
    {
        var store = new RecordingOutputCacheStore { FailingTag = TagA };
        await using var services = BuildServices(store, logger: null);
        var context = CreateContext(HttpMethods.Put, new NoContentResult(), services);

        var act = async () => await RunAsync(new EvictOutputCacheAttribute(TagA), context);

        await act.Should().NotThrowAsync("without a registered logger the failure is reported to a null logger");
    }

    // -- Rule 9: once per request --
    [Fact]
    public async Task ClassAndMethodInstancesSameTag_EvictedOnce()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var context = CreateContext(HttpMethods.Put, new NoContentResult(), services);
        var classLevel = new EvictOutputCacheAttribute("shared", "class-only");
        var methodLevel = new EvictOutputCacheAttribute("shared", "method-only");

        // MVC nests the method-level filter inside the class-level one.
        await classLevel.OnResultExecutionAsync(context, async () =>
        {
            await RunAsync(methodLevel, context);
            return new ResultExecutedContext(context, context.Filters, context.Result, context.Controller);
        });

        store.EvictedTags.Should().BeEquivalentTo("shared", "class-only", "method-only");
        store.EvictedTags.Should().OnlyHaveUniqueItems("a tag named at both levels is evicted once per request");
    }

    [Fact]
    public async Task TagAlreadyRecordedInItems_IsSkipped()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var context = CreateContext(HttpMethods.Put, new NoContentResult(), services);
        context.HttpContext.Items[EvictOutputCacheAttribute.EvictedTagsItemKey] = new HashSet<string>(StringComparer.Ordinal) { TagA };

        await RunAsync(new EvictOutputCacheAttribute(TagA, TagB), context);

        store.EvictedTags.Should().Equal(TagB);
    }

    [Fact]
    public async Task EvictedTags_AreRecordedInHttpContextItems()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var context = CreateContext(HttpMethods.Put, new NoContentResult(), services);

        await RunAsync(new EvictOutputCacheAttribute(TagA, TagB), context);

        context.HttpContext.Items[EvictOutputCacheAttribute.EvictedTagsItemKey]
            .Should().BeOfType<HashSet<string>>()
            .Which.Should().BeEquivalentTo(TagA, TagB);
    }

    [Fact]
    public async Task TagsDifferingOnlyInCase_AreBothEvicted()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var context = CreateContext(HttpMethods.Put, new NoContentResult(), services);

        await new EvictOutputCacheAttribute("Probe:Tag").OnResultExecutionAsync(context, async () =>
        {
            await RunAsync(new EvictOutputCacheAttribute("probe:tag"), context);
            return new ResultExecutedContext(context, context.Filters, context.Result, context.Controller);
        });

        store.EvictedTags.Should().BeEquivalentTo(["Probe:Tag", "probe:tag"], "tags are compared ordinally");
    }

    // -- Rule 10: the dedupe is per request, so a replayed 2xx evicts again --
    // DESIGN-QUESTION: rule 10 could mean a TestServer replay through IdempotencyFilter; pinned at unit level as "a new request with a 2xx result evicts again".
    [Fact]
    public async Task SecondRequestWithSameTags_EvictsAgain()
    {
        var store = new RecordingOutputCacheStore();
        await using var services = BuildServices(store);
        var sut = new EvictOutputCacheAttribute(TagA);

        await RunAsync(sut, CreateContext(HttpMethods.Put, new NoContentResult(), services));
        await RunAsync(sut, CreateContext(HttpMethods.Put, new NoContentResult(), services));

        store.EvictedTags.Should().Equal(
            [TagA, TagA],
            "one attribute instance serves every request, and an idempotent replay is a new request with a 2xx result");
    }

    // -- Pipeline cases (TestServer) --
    [Fact]
    public async Task Put_204_ThroughPipeline_Evicts()
    {
        var store = new RecordingOutputCacheStore();
        await using var app = await CreateHostAsync(store);
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/{EvictOutputCacheProbeController.Root}/succeeding/1", UriKind.Relative));
        request.Headers.Add(ConcurrencyETag.IfMatchHeaderName, ConcurrencyETag.Format([0, 0, 0, 0, 0, 0, 7, 209]));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        store.EvictedTags.Should().Equal(EvictOutputCacheProbeController.Tag);
    }

    [Fact]
    public async Task Put_409RewrittenTo412BySupportsIfMatch_DoesNotEvict()
    {
        var store = new RecordingOutputCacheStore();
        await using var app = await CreateHostAsync(store);
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/{EvictOutputCacheProbeController.Root}/conflicting/1", UriKind.Relative));
        request.Headers.Add(ConcurrencyETag.IfMatchHeaderName, ConcurrencyETag.Format([0, 0, 0, 0, 0, 0, 7, 209]));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed, "the action filter rewrote the conflict (precondition)");
        store.Evictions.Should().BeEmpty("the result filter sees the final 412, a failed write");
    }

    [Fact]
    public async Task ActionThrows_DoesNotEvict()
    {
        var store = new RecordingOutputCacheStore();
        await using var app = await CreateHostAsync(store);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            new Uri($"/{EvictOutputCacheProbeController.Root}/throwing", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the probe middleware answers the unhandled exception (precondition)");
        store.Evictions.Should().BeEmpty("an unhandled action exception skips result filters");
    }

    // -- Helpers --
    private static ServiceProvider BuildServices(
        IOutputCacheStore? store,
        ILogger<EvictOutputCacheAttribute>? logger = null)
    {
        var services = new ServiceCollection();
        if (store is not null)
        {
            services.AddSingleton(store);
        }

        if (logger is not null)
        {
            services.AddSingleton(logger);
        }

        return services.BuildServiceProvider();
    }

    private static ResultExecutingContext CreateContext(
        string method,
        IActionResult result,
        IServiceProvider services,
        int responseStatusCode = StatusCodes.Status200OK)
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Method = method;
        httpContext.Response.StatusCode = responseStatusCode;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ResultExecutingContext(actionContext, [], result, controller: new object());
    }

    /// <summary>Runs the filter and reports whether it called <c>next()</c>.</summary>
    private static async Task<bool> RunAsync(
        EvictOutputCacheAttribute sut,
        ResultExecutingContext context,
        List<string>? journal = null)
    {
        var nextCalled = false;

        await sut.OnResultExecutionAsync(context, () =>
        {
            nextCalled = true;
            journal?.Add("next");
            return Task.FromResult(new ResultExecutedContext(context, context.Filters, context.Result, context.Controller));
        });

        return nextCalled;
    }

    private static async Task<WebApplication> CreateHostAsync(RecordingOutputCacheStore store)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IOutputCacheStore>(store);
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (var i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                {
                    manager.FeatureProviders.RemoveAt(i);
                }
            }

            manager.FeatureProviders.Add(new ProbeControllerFeatureProvider());
        });

        var app = builder.Build();

        // Answers the probe's deliberate exception as a 500 so the test client sees a response.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (NotSupportedException)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        });

        app.MapControllers();
        await app.StartAsync();

        return app;
    }

    private sealed class ProbeControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Add(typeof(EvictOutputCacheProbeController).GetTypeInfo());
    }
}

/// <summary>
/// An output-cache store that records every eviction (tag and token), optionally into a shared
/// journal so a test can order evictions against <c>next()</c>, and can fail one tag.
/// </summary>
internal sealed class RecordingOutputCacheStore(List<string>? journal = null) : IOutputCacheStore
{
    public string? FailingTag { get; init; }

    public List<(string Tag, CancellationToken Token)> Evictions { get; } = [];

    public IReadOnlyList<string> EvictedTags => [.. Evictions.Select(e => e.Tag)];

    public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        if (string.Equals(tag, FailingTag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("cache store unavailable");
        }

        Evictions.Add((tag, cancellationToken));
        journal?.Add($"evict:{tag}");
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);

    public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

/// <summary>A logger that records the level of every entry written.</summary>
/// <typeparam name="T">The logger category.</typeparam>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogLevel> Levels { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
}

/// <summary>Class-level eviction over three conditional and unconditional writes.</summary>
[ApiController]
[Route(EvictOutputCacheProbeController.Root)]
[EvictOutputCache(EvictOutputCacheProbeController.Tag)]
public sealed class EvictOutputCacheProbeController : ControllerBase
{
    public const string Root = "evict-output-cache-probe";
    public const string Tag = "probe:things";

    [HttpPut("succeeding/{id}")]
    [SupportsIfMatch]
    public IActionResult PutSucceeding(int id)
    {
        _ = id;
        return NoContent();
    }

    [HttpPut("conflicting/{id}")]
    [SupportsIfMatch]
    public IActionResult PutConflicting(int id)
    {
        _ = id;
        return Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Operation failed" });
    }

    [HttpPost("throwing")]
    public IActionResult PostThrowing() => throw new NotSupportedException("the action failed");
}
