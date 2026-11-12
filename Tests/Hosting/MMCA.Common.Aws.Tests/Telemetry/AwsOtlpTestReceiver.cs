using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MMCA.Common.Aws.Tests.Telemetry;

/// <summary>
/// A minimal in-process OTLP/HTTP receiver on a loopback port, standing in for the X-Ray and
/// CloudWatch Logs OTLP endpoints (LocalStack serves neither). It records the path, every header and
/// the body of each request and answers 200 with an empty protobuf body. Same shape as
/// <c>MMCA.Common.Gcp.Tests/Telemetry/OtlpTestReceiver.cs</c>, recording all headers instead of two.
/// </summary>
internal sealed class AwsOtlpTestReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;

    private AwsOtlpTestReceiver(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>The root URL; the signal URLs are <c>v1/traces</c> and <c>v1/logs</c> under it.</summary>
    public Uri BaseAddress { get; }

    /// <summary>Every request received, in arrival order.</summary>
    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();

    public static async Task<AwsOtlpTestReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();

        AwsOtlpTestReceiver? receiver = null;
        app.Run(async context =>
        {
            await using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            var headers = context.Request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);
            receiver!.Requests.Enqueue(new ReceivedRequest(
                context.Request.Path.Value ?? string.Empty,
                headers,
                context.Request.ContentType,
                body.ToArray()));
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/x-protobuf";
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        receiver = new AwsOtlpTestReceiver(app, new Uri(address));
        return receiver;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>One received OTLP request.</summary>
    internal sealed record ReceivedRequest(string Path, IReadOnlyDictionary<string, string> Headers, string? ContentType, byte[] Body)
    {
        public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;
    }
}
