using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers.TwoFactor;
using MMCA.Common.API.Tests.Authorization.MultiFactor;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.Users;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;

namespace MMCA.Common.API.Tests.Controllers.TwoFactor;

/// <summary>
/// A real ASP.NET Core host over <see cref="TwoFactorControllerBase{TBeginCommand, TConfirmCommand, TDisableCommand, TRegenerateCommand}"/>:
/// MVC routing and filters, the framework's authorization policies and a header authentication
/// scheme. The four command handlers and the store are recording doubles whose answers a test sets.
/// </summary>
internal sealed class TwoFactorControllerTestHost : IAsyncDisposable
{
    public const string Route = "auth/two-factor";
    public const UserIdentifierType UserId = 7;

    private WebApplication _app = null!;

    private TwoFactorControllerTestHost()
    {
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

    public RecordingHandler<BeginEnrollmentCommand, Result<TwoFactorSetupResponse>> Begin { get; } =
        new(Result.Success(new TwoFactorSetupResponse("JBSWY3DPEHPK3PXP", "otpauth://totp/MMCA:user%40example.com?secret=JBSWY3DPEHPK3PXP")));

    public RecordingHandler<ConfirmEnrollmentCommand, Result<TwoFactorRecoveryCodesResponse>> Confirm { get; } =
        new(Result.Success(new TwoFactorRecoveryCodesResponse(["code-1", "code-2"])));

    public RecordingHandler<DisableTwoFactorCommand, Result> Disable { get; } = new(Result.Success());

    public RecordingHandler<RegenerateRecoveryCodesCommand, Result<TwoFactorRecoveryCodesResponse>> Regenerate { get; } =
        new(Result.Success(new TwoFactorRecoveryCodesResponse(["fresh-1", "fresh-2", "fresh-3"])));

    public FakeTwoFactorStore Store { get; } = new();

    /// <summary>Starts the host; <paramref name="configureServices"/> adds what a test needs beyond the doubles.</summary>
    public static async Task<TwoFactorControllerTestHost> StartAsync(Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var host = new TwoFactorControllerTestHost();
        builder.Services.AddSingleton<TimeProvider>(host.Clock);
        builder.Services.AddSingleton<ICommandHandler<BeginEnrollmentCommand, Result<TwoFactorSetupResponse>>>(host.Begin);
        builder.Services.AddSingleton<ICommandHandler<ConfirmEnrollmentCommand, Result<TwoFactorRecoveryCodesResponse>>>(host.Confirm);
        builder.Services.AddSingleton<ICommandHandler<DisableTwoFactorCommand, Result>>(host.Disable);
        builder.Services.AddSingleton<ICommandHandler<RegenerateRecoveryCodesCommand, Result<TwoFactorRecoveryCodesResponse>>>(host.Regenerate);
        builder.Services.AddSingleton<ITwoFactorStore>(host.Store);
        configureServices?.Invoke(builder.Services);

        builder.Services.AddAuthentication(MultiFactorTestPrincipals.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimsAuthenticationHandler>(MultiFactorTestPrincipals.SchemeName, configureOptions: null);
        builder.Services.AddAuthorizationPolicies();
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (var i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                {
                    manager.FeatureProviders.RemoveAt(i);
                }
            }

            manager.FeatureProviders.Add(new RoutedTwoFactorControllerFeatureProvider());
        });

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync(Xunit.TestContext.Current.CancellationToken);

        host._app = app;
        return host;
    }

    /// <summary>A client signed in as <see cref="UserId"/>, <paramref name="signedInAgo"/> ago (no <c>auth_time</c> when null).</summary>
    public HttpClient SignedIn(TimeSpan? signedInAgo, params Claim[] extra)
    {
        List<Claim> claims = [new(AuthClaimTypes.Subject, UserId.ToString(CultureInfo.InvariantCulture))];
        if (signedInAgo is { } ago)
        {
            claims.Add(new Claim(
                AuthClaimTypes.AuthTime,
                (Clock.GetUtcNow() - ago).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }

        claims.AddRange(extra);
        return WithClaims([.. claims]);
    }

    /// <summary>A client presenting exactly <paramref name="claims"/>.</summary>
    public HttpClient WithClaims(params Claim[] claims)
    {
        var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Add(MultiFactorTestPrincipals.ClaimsHeader, MultiFactorTestPrincipals.Header(claims));
        return client;
    }

    public HttpClient Anonymous() => _app.GetTestClient();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private sealed class RoutedTwoFactorControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) =>
            feature.Controllers.Add(typeof(RoutedTwoFactorController).GetTypeInfo());
    }
}

internal sealed record BeginEnrollmentCommand(UserIdentifierType UserId) : IUserScopedRequest;

internal sealed record ConfirmEnrollmentCommand(UserIdentifierType UserId, TwoFactorCodeRequest Request) : IUserScopedCommand<TwoFactorCodeRequest>;

internal sealed record DisableTwoFactorCommand(UserIdentifierType UserId, TwoFactorCodeRequest Request) : IUserScopedCommand<TwoFactorCodeRequest>;

internal sealed record RegenerateRecoveryCodesCommand(UserIdentifierType UserId, TwoFactorCodeRequest Request) : IUserScopedCommand<TwoFactorCodeRequest>;

// Internal so the default controller discovery of other test hosts never picks it up. It carries the
// class-level attributes and the one-line factories a consumer's sealed subclass declares.
[ApiController]
[Route(TwoFactorControllerTestHost.Route)]
internal sealed class RoutedTwoFactorController(
    ICommandHandler<BeginEnrollmentCommand, Result<TwoFactorSetupResponse>> beginHandler,
    ICommandHandler<ConfirmEnrollmentCommand, Result<TwoFactorRecoveryCodesResponse>> confirmHandler,
    ICommandHandler<DisableTwoFactorCommand, Result> disableHandler,
    ICommandHandler<RegenerateRecoveryCodesCommand, Result<TwoFactorRecoveryCodesResponse>> regenerateHandler,
    ITwoFactorStore store)
    : TwoFactorControllerBase<BeginEnrollmentCommand, ConfirmEnrollmentCommand, DisableTwoFactorCommand, RegenerateRecoveryCodesCommand>(
        beginHandler, confirmHandler, disableHandler, regenerateHandler, store)
{
    protected override BeginEnrollmentCommand CreateBeginEnrollmentCommand(UserIdentifierType userId) => new(userId);

    protected override ConfirmEnrollmentCommand CreateConfirmEnrollmentCommand(UserIdentifierType userId, TwoFactorCodeRequest request) => new(userId, request);

    protected override DisableTwoFactorCommand CreateDisableCommand(UserIdentifierType userId, TwoFactorCodeRequest request) => new(userId, request);

    protected override RegenerateRecoveryCodesCommand CreateRegenerateRecoveryCodesCommand(UserIdentifierType userId, TwoFactorCodeRequest request) => new(userId, request);
}

/// <summary>Records every command it receives and answers <see cref="Answer"/>.</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
internal sealed class RecordingHandler<TCommand, TResult>(TResult answer) : ICommandHandler<TCommand, TResult>
{
    private readonly List<TCommand> _received = [];

    public TResult Answer { get; set; } = answer;

    public IReadOnlyList<TCommand> Received
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    public Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        lock (_received)
        {
            _received.Add(command);
        }

        return Task.FromResult(Answer);
    }
}

/// <summary>The app's two-factor state for the status action; every write is outside these tests.</summary>
internal sealed class FakeTwoFactorStore : ITwoFactorStore
{
    public ITwoFactorUserState? State { get; set; }

    public Task<ITwoFactorUserState?> GetAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(userId == TwoFactorControllerTestHost.UserId ? State : null);

    public Task<Result> StartEnrollmentAsync(UserIdentifierType userId, string secret, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> CompleteEnrollmentAsync(UserIdentifierType userId, IReadOnlyCollection<string> recoveryCodeHashes, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> DisableAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> ReplaceRecoveryCodesAsync(UserIdentifierType userId, IReadOnlyCollection<string> recoveryCodeHashes, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> ConsumeRecoveryCodeAsync(UserIdentifierType userId, string recoveryCodeHash, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>A user's two-factor state.</summary>
internal sealed record TwoFactorState(bool IsTwoFactorEnabled, string? TwoFactorSecret, IReadOnlyCollection<string> TwoFactorRecoveryCodeHashes)
    : ITwoFactorUserState;
