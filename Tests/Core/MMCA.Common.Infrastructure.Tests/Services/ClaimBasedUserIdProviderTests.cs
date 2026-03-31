using System.IO.Pipelines;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Infrastructure.Services;

namespace MMCA.Common.Infrastructure.Tests.Services;

public sealed class ClaimBasedUserIdProviderTests : IAsyncDisposable
{
    private readonly ClaimBasedUserIdProvider _sut = new();
    private readonly List<TestConnectionContext> _contexts = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var ctx in _contexts)
        {
            await ctx.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Extracts user_id claim ──
    [Fact]
    public void GetUserId_WhenUserIdClaimExists_ReturnsClaimValue()
    {
        var claims = new[] { new Claim("user_id", "42") };
        var connection = CreateHubConnectionContext(claims);

        string? userId = _sut.GetUserId(connection);

        userId.Should().Be("42");
    }

    // ── No user_id claim ──
    [Fact]
    public void GetUserId_WhenNoUserIdClaim_ReturnsNull()
    {
        var claims = new[] { new Claim("sub", "42") };
        var connection = CreateHubConnectionContext(claims);

        string? userId = _sut.GetUserId(connection);

        userId.Should().BeNull();
    }

    // ── No claims at all ──
    [Fact]
    public void GetUserId_WhenNoClaims_ReturnsNull()
    {
        var connection = CreateHubConnectionContext([]);

        string? userId = _sut.GetUserId(connection);

        userId.Should().BeNull();
    }

    // ── Helper ──
    private HubConnectionContext CreateHubConnectionContext(Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "test");
        var principal = new ClaimsPrincipal(identity);

        var connectionContext = new TestConnectionContext(principal);
        _contexts.Add(connectionContext);

        return new HubConnectionContext(
            connectionContext,
            new HubConnectionContextOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15),
            },
            NullLoggerFactory.Instance);
    }
}

// ── Test helpers ──
public sealed class TestConnectionContext : ConnectionContext, IConnectionUserFeature
{
    private readonly Pipe _pipe = new();

    public TestConnectionContext(ClaimsPrincipal user)
    {
        User = user;
        Features = new FeatureCollection();
        Features.Set<IConnectionUserFeature>(this);
        Items = new Dictionary<object, object?>();
        Transport = new TestDuplexPipe(_pipe.Reader, _pipe.Writer);
    }

    public override string ConnectionId { get; set; } = "test-connection";
    public override IFeatureCollection Features { get; }
    public override IDictionary<object, object?> Items { get; set; }
    public override IDuplexPipe Transport { get; set; }
    public ClaimsPrincipal? User { get; set; }

    public override async ValueTask DisposeAsync()
    {
        await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
        await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class TestDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
{
    public PipeReader Input { get; } = input;
    public PipeWriter Output { get; } = output;
}
