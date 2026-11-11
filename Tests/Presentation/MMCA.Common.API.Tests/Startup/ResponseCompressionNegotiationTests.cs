using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// In-memory host tests for the encoding a client actually receives from a host that calls
/// <c>AddCommonResponseCompression()</c> and <c>UseResponseCompression()</c>: when a client weights
/// encodings equally the server's provider order decides, so on .NET 11 Zstandard wins over Brotli,
/// and Brotli still wins over Gzip for a client that does not offer Zstandard.
/// </summary>
public sealed class ResponseCompressionNegotiationTests
{
    private const string PayloadPath = "/payload";

    [Fact]
    public async Task ResponseCompression_WhenClientAcceptsZstdBrotliAndGzip_PrefersTheNewestEncoding()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await GetPayloadAsync(client, "zstd, br, gzip");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
#if NET11_0_OR_GREATER
        response.Content.Headers.ContentEncoding.Should().Equal("zstd");
#else
        response.Content.Headers.ContentEncoding.Should().Equal("br");
#endif
    }

    [Fact]
    public async Task ResponseCompression_WhenClientAcceptsBrotliAndGzip_UsesBrotli()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await GetPayloadAsync(client, "br, gzip");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().Equal("br");
    }

    private static async Task<HttpResponseMessage> GetPayloadAsync(HttpClient client, string acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(PayloadPath, UriKind.Relative));
        request.Headers.Add("Accept-Encoding", acceptEncoding);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Services.AddCommonResponseCompression();
        var app = builder.Build();
        app.UseResponseCompression();

        // A few KB of highly compressible text, of a MIME type every provider compresses by default.
        app.MapGet(PayloadPath, static () => Results.Text(new string('a', 8192), "text/plain"));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
