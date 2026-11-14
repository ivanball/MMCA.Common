using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using Moq;

namespace MMCA.Common.API.Tests.Uploads;

/// <summary>
/// Run 17 A-64: a form body over the endpoint's <c>[RequestSizeLimit]</c> answers 413 "Payload Too
/// Large", not the 400 model-state error "Failed to read the request form". Boots the real
/// <c>AddAPI</c> pipeline on Kestrel, because only a real server enforces the body size limit.
/// </summary>
public sealed class OversizeFormResourceFilterTests
{
    private const int LimitBytes = 1024;

    [Fact]
    public async Task AnOversizeFileUpload_Answers413BeforeModelBinding()
    {
        await using var app = await StartHostAsync();
        using var client = CreateClient(app);

        using var response = await client.PostAsync(
            new Uri("/probe-upload", UriKind.Relative),
            FileForm(LimitBytes * 4),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("Payload Too Large").And.NotContain("Failed to read the request form");
    }

    [Fact]
    public async Task AFileUploadWithinTheLimit_StillBindsAndReachesTheAction()
    {
        await using var app = await StartHostAsync();
        using var client = CreateClient(app);

        using var response = await client.PostAsync(
            new Uri("/probe-upload", UriKind.Relative),
            FileForm(100),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be("100");
    }

    [Fact]
    public async Task AnActionThatBindsNoForm_IsNotReadByTheFilter()
    {
        await using var app = await StartHostAsync();
        using var client = CreateClient(app);

        // The streaming action reads the body itself; a buffered form read would leave it empty.
        using var response = await client.PostAsync(
            new Uri("/probe-stream", UriKind.Relative),
            FileForm(100),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        int.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeGreaterThan(100, "the raw multipart body reached the action unread");
    }

    private static MultipartFormDataContent FileForm(int size)
    {
        var file = new ByteArrayContent(new byte[size]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { file, "file", "avatar.png" } };
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();

        builder.Services.AddAPI();
        builder.Services.AddCommonExceptionHandlers();

        // AddAPI registers two scoped filters that take infrastructure services this host does not have.
        builder.Services.AddScoped(_ => Mock.Of<ICurrentUserService>());
        builder.Services.AddScoped(_ => Mock.Of<ICacheService>());

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
        app.UseExceptionHandler();
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class ProbeControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
            => feature.Controllers.Add(typeof(UploadProbeController).GetTypeInfo());
    }
}

/// <summary>An avatar-shaped upload behind a small request size limit, plus a streaming action.</summary>
[ApiController]
[Route("")]
public sealed class UploadProbeController : ControllerBase
{
    /// <summary>Binds one form file, as an avatar upload does.</summary>
    /// <param name="file">The uploaded file.</param>
    /// <returns>The file's length.</returns>
    [HttpPost("probe-upload")]
    [RequestSizeLimit(1024)]
    public ActionResult<string> Upload(IFormFile file) => Ok(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Reads the raw body itself and binds nothing from the form.</summary>
    /// <returns>The number of body bytes read.</returns>
    [HttpPost("probe-stream")]
    public async Task<ActionResult<string>> StreamAsync()
    {
        await using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, HttpContext.RequestAborted);
        return Ok(buffer.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
