using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Gcp.Tests.Telemetry;

/// <summary>
/// A minimal in-process OTLP/HTTP receiver on a loopback port, standing in for
/// <c>telemetry.googleapis.com</c> (which has no emulator). It records the path, the two Google
/// headers and the protobuf body of every request and answers 200 with an empty
/// <c>Export*ServiceResponse</c>, which is the empty protobuf message.
/// </summary>
internal sealed class OtlpTestReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;

    /// <summary>Released once per recorded request, so a test can wait for an arrival instead of sleeping.</summary>
    private readonly SemaphoreSlim _arrived = new(0);

    private OtlpTestReceiver(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>The root URL to set as <c>GoogleCloudTelemetry:Endpoint</c>.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Every request received, in arrival order.</summary>
    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();

    public static async Task<OtlpTestReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();

        OtlpTestReceiver? receiver = null;
        app.Run(async context =>
        {
            await using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            receiver!.Requests.Enqueue(new ReceivedRequest(
                context.Request.Path.Value ?? string.Empty,
                context.Request.Headers.Authorization.ToString(),
                context.Request.Headers["x-goog-user-project"].ToString(),
                context.Request.ContentType,
                body.ToArray()));
            receiver._arrived.Release();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/x-protobuf";
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        receiver = new OtlpTestReceiver(app, new Uri(address));
        return receiver;
    }

    /// <summary>
    /// Waits until a request to <paramref name="path"/> has been recorded, or <paramref name="timeout"/>
    /// passes. Returns whether one arrived; the caller's own assertions report a miss.
    /// </summary>
    public async Task<bool> WaitForRequestAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            while (!Requests.Any(request => string.Equals(request.Path, path, StringComparison.Ordinal)))
            {
                await _arrived.WaitAsync(bounded.Token);
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _arrived.Dispose();
    }

    /// <summary>One received OTLP request.</summary>
    internal sealed record ReceivedRequest(string Path, string Authorization, string QuotaProject, string? ContentType, byte[] Body);
}
