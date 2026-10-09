using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.API.RateLimiting;
using MMCA.Common.API.Startup;
using MMCA.Common.Shared.Auth;
using Moq;
using StackExchange.Redis;

namespace MMCA.Common.API.Tests.RateLimiting;

/// <summary>
/// Drives the shared Redis counter through the real ASP.NET Core rate-limiting middleware rather
/// than calling the limiter directly. The middleware tries the synchronous <c>AttemptAcquire</c>
/// first and only falls through to <c>AcquireAsync</c> when that lease is not acquired, so a limiter
/// whose synchronous path granted would admit every request without ever counting it. A unit test
/// on the limiter alone cannot see that; these requests can.
/// </summary>
public sealed class DistributedRateLimitingMiddlewareTests
{
    [Fact]
    public async Task AuthenticatedUser_OverTheGlobalLimit_IsRejectedByTheSharedCounter()
    {
        var (connection, database) = CreateConnection(1, 2, 3);
        await using var app = await CreateHostAsync(
            connection.Object,
            new RateLimitingSettings { GlobalPermitLimit = 2, Distributed = true });
        using var client = app.GetTestClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/probe", UriKind.Relative));
            request.Headers.Add(HeaderAuthenticationHandler.UserHeader, "alice");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
        database.Verify(
            d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task AnonymousHubRequest_OverTheHubLimit_IsRejectedByTheSharedCounter()
    {
        var (connection, database) = CreateConnection(1, 2);
        await using var app = await CreateHostAsync(
            connection.Object,
            new RateLimitingSettings { AnonymousHubPermitLimit = 1, Distributed = true });
        using var client = app.GetTestClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.GetAsync(
                new Uri("/hubs/notifications", UriKind.Relative),
                TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
        database.Verify(
            d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()),
            Times.Exactly(2));
    }

    private static async Task<WebApplication> CreateHostAsync(IConnectionMultiplexer connection, RateLimitingSettings settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(connection);
        builder.Services.AddAuthentication(HeaderAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(HeaderAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddCommonRateLimiting(settings);

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.MapGet("/probe", () => Results.Text("ok"));
        app.MapGet("/hubs/notifications", () => Results.Text("ok"));
        await app.StartAsync();

        return app;
    }

    private static (Mock<IConnectionMultiplexer> Connection, Mock<IDatabase> Database) CreateConnection(params long[] incrementResults)
    {
        var database = new Mock<IDatabase>();
        var sequence = database.SetupSequence(d => d.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()));
        foreach (var result in incrementResults)
        {
            sequence = sequence.ReturnsAsync(result);
        }

        database.Setup(d => d.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        var connection = new Mock<IConnectionMultiplexer>();
        connection.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(database.Object);

        return (connection, database);
    }

    /// <summary>
    /// Authenticates a request carrying <see cref="UserHeader"/> with that value as the subject
    /// claim (the global limiter's partition key); everything else is anonymous.
    /// </summary>
    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "HeaderTest";
        public const string UserHeader = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrEmpty(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(AuthClaimTypes.Subject, user.ToString())], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
