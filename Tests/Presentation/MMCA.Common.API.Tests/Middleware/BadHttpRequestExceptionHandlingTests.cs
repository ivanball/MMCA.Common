using System.Collections.Concurrent;
using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.API.Tests.Middleware;

/// <summary>
/// X-24 (Store run 1, B9): a request body over an endpoint's <c>RequestSizeLimit</c> makes Kestrel
/// throw <see cref="BadHttpRequestException"/> with status 413 while the action binds the body. The
/// framework's exception pipeline (<c>AddCommonExceptionHandlers</c>) must surface that exception's
/// own status as Problem Details (413 for the size case, 400 for any other bad request), and log it
/// at Warning: it is a caller fault, not a server fault, so it must neither answer 500 nor log Error.
/// </summary>
public sealed class BadHttpRequestExceptionHandlingTests
{
    [Fact]
    public async Task OversizeBody_AnswersTheExceptionsOwn413_AsProblemDetails()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = await CreateHostAsync(logs);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            new Uri("/oversize", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(
            HttpStatusCode.RequestEntityTooLarge,
            "a body over the endpoint's size limit is the caller's fault and BadHttpRequestException already carries 413");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("\"status\":413");
    }

    [Fact]
    public async Task OtherBadRequest_AnswersTheExceptionsOwn400_AsProblemDetails()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = await CreateHostAsync(logs);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            new Uri("/malformed", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "BadHttpRequestException defaults to 400 and that status is the answer, not 500");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task OversizeBody_IsLoggedAtWarning_NotError()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = await CreateHostAsync(logs);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            new Uri("/oversize", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        var frameworkEntries = logs.Entries
            .Where(e => e.Category.StartsWith("MMCA.", StringComparison.Ordinal))
            .ToList();

        frameworkEntries.Should().NotContain(
            e => e.Level >= LogLevel.Error,
            "a rejected oversize body is routine caller error and must not page anyone as an Error");
        frameworkEntries.Should().Contain(
            e => e.Level == LogLevel.Warning,
            "the rejection is still worth a Warning so an operator can see it happening");
    }

    [Fact]
    public async Task UnrelatedException_StillAnswers500()
    {
        var logs = new RecordingLoggerProvider();
        await using var app = await CreateHostAsync(logs);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            new Uri("/boom", UriKind.Relative),
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "only BadHttpRequestException is a caller fault");
    }

    private static async Task<WebApplication> CreateHostAsync(RecordingLoggerProvider logs)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);

        builder.Services.AddCommonExceptionHandlers();

        var app = builder.Build();
        app.UseExceptionHandler();

        // What Kestrel throws while the action binds a body over the endpoint's RequestSizeLimit.
        app.MapPost("/oversize", IResult () => throw new BadHttpRequestException(
            "Request body too large. The max request body size is 1024 bytes.",
            StatusCodes.Status413PayloadTooLarge));
        app.MapPost("/malformed", IResult () => throw new BadHttpRequestException("Malformed request body."));
        app.MapPost("/boom", IResult () => throw new InvalidOperationException("server fault"));
        await app.StartAsync();

        return app;
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }
}
