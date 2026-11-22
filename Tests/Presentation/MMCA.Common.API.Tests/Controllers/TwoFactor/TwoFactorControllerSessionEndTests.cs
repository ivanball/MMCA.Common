using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.TwoFactor;

/// <summary>
/// Turning the second factor on or off ends the account's sessions: a confirmed enrollment and a
/// disable revoke every refresh session of the account, and the caller still gets its answer when the
/// revocation fails (logged without the address) or when the host has no authentication service.
/// Regenerating recovery codes and a failed confirm or disable revoke nothing.
/// </summary>
public sealed class TwoFactorControllerSessionEndTests : IDisposable
{
    private const string Address = "owner@example.com";
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    private readonly Mock<IAuthenticationService> _authentication = new();
    private readonly RecordingLoggerProvider _logs = new();

    public TwoFactorControllerSessionEndTests() =>
        _authentication
            .Setup(x => x.RevokeAllSessionsAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _logs.Dispose();

    [Fact]
    public async Task ConfirmSucceeded_RevokesEverySessionOfTheAccount_AndReturnsTheRecoveryCodes()
    {
        await using var host = await StartAsync();
        using var client = SignedIn(host);

        using var response = await PostAsync(client, "/enrollment/confirm");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TwoFactorRecoveryCodesResponse>(Ct))!.RecoveryCodes.Should().Equal("code-1", "code-2");
        VerifyRevokedOnlyForTheSignedInAccount(host.Confirm.Received.Should().ContainSingle().Subject.UserId);
    }

    [Fact]
    public async Task ConfirmSucceeded_RevocationFails_LogsWithoutTheAddress_AndStillReturnsTheRecoveryCodes()
    {
        _authentication
            .Setup(x => x.RevokeAllSessionsAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Failure("Sessions.StoreUnavailable", "Could not reach the store for " + Address)));
        await using var host = await StartAsync();
        using var client = SignedIn(host);

        using var response = await PostAsync(client, "/enrollment/confirm");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TwoFactorRecoveryCodesResponse>(Ct))!.RecoveryCodes.Should().Equal("code-1", "code-2");
        AssertOneWarningWithoutTheAddress(host);
    }

    [Fact]
    public async Task ConfirmSucceeded_RevocationThrows_LogsWithoutTheAddressOrTheException_AndStillReturnsTheRecoveryCodes()
    {
        _authentication
            .Setup(x => x.RevokeAllSessionsAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Session store refused " + Address));
        await using var host = await StartAsync();
        using var client = SignedIn(host);

        using var response = await PostAsync(client, "/enrollment/confirm");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TwoFactorRecoveryCodesResponse>(Ct))!.RecoveryCodes.Should().Equal("code-1", "code-2");
        var warning = AssertOneWarningWithoutTheAddress(host);
        warning.Exception.Should().BeNull("the exception, whose message may carry the address, is not logged");
    }

    [Fact]
    public async Task DisableSucceeded_RevokesEverySessionOfTheAccount()
    {
        await using var host = await StartAsync();
        using var client = SignedIn(host);

        using var response = await PostAsync(client, "/disable");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        VerifyRevokedOnlyForTheSignedInAccount(host.Disable.Received.Should().ContainSingle().Subject.UserId);
    }

    [Fact]
    public async Task RegenerateRecoveryCodes_RevokesNothing()
    {
        await using var host = await StartAsync();
        using var client = SignedIn(host);

        using var response = await PostAsync(client, "/recovery-codes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        VerifyNothingRevoked();
    }

    [Fact]
    public async Task ConfirmFailed_RevokesNothing()
    {
        await using var host = await StartAsync();
        host.Confirm.Answer = Result.Failure<TwoFactorRecoveryCodesResponse>(TwoFactorErrors.TwoFactorInvalid());
        using var client = SignedIn(host);

        using var response = await PostAsync(client, "/enrollment/confirm");

        response.IsSuccessStatusCode.Should().BeFalse();
        VerifyNothingRevoked();
    }

    [Fact]
    public async Task DisableFailed_RevokesNothing()
    {
        await using var host = await StartAsync();
        host.Disable.Answer = Result.Failure(TwoFactorErrors.TwoFactorInvalid());
        using var client = SignedIn(host);

        using var response = await PostAsync(client, "/disable");

        response.IsSuccessStatusCode.Should().BeFalse();
        VerifyNothingRevoked();
    }

    [Fact]
    public async Task HostWithoutTheAuthenticationService_ConfirmStillReturnsTheRecoveryCodes()
    {
        await using var host = await TwoFactorControllerTestHost.StartAsync(services => services.AddSingleton<ILoggerProvider>(_logs));
        using var client = SignedIn(host);

        using var response = await PostAsync(client, "/enrollment/confirm");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TwoFactorRecoveryCodesResponse>(Ct))!.RecoveryCodes.Should().Equal("code-1", "code-2");
        ControllerEntries().Should().NotContain(entry => entry.Level >= LogLevel.Warning);
    }

    private static HttpClient SignedIn(TwoFactorControllerTestHost host) =>
        host.SignedIn(Fresh, new Claim(ClaimTypes.Email, Address));

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string action) =>
        client.PostAsJsonAsync(TwoFactorControllerTestHost.Route + action, new TwoFactorCodeRequest("654321"), Ct);

    private Task<TwoFactorControllerTestHost> StartAsync() =>
        TwoFactorControllerTestHost.StartAsync(services =>
        {
            services.AddSingleton(_authentication.Object);
            services.AddSingleton<ILoggerProvider>(_logs);
        });

    // Exactly one revocation, for the account the token names (which is also the account the command
    // carried), and none for any other id.
    private void VerifyRevokedOnlyForTheSignedInAccount(UserIdentifierType commandUserId)
    {
        commandUserId.Should().Be(TwoFactorControllerTestHost.UserId, "the command carries the signed-in account");
        _authentication.Verify(
            x => x.RevokeAllSessionsAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()),
            Times.Once());
        _authentication.Verify(
            x => x.RevokeAllSessionsAsync(TwoFactorControllerTestHost.UserId, It.IsAny<CancellationToken>()),
            Times.Once());
        _authentication.Verify(
            x => x.RevokeAllSessionsAsync(
                It.Is<UserIdentifierType>(id => id != TwoFactorControllerTestHost.UserId),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }

    private void VerifyNothingRevoked() =>
        _authentication.Verify(
            x => x.RevokeAllSessionsAsync(It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()),
            Times.Never());

    private LogEntry AssertOneWarningWithoutTheAddress(TwoFactorControllerTestHost host)
    {
        VerifyRevokedOnlyForTheSignedInAccount(host.Confirm.Received.Should().ContainSingle().Subject.UserId);
        var warning = ControllerEntries().Should().ContainSingle(entry => entry.Level == LogLevel.Warning).Subject;
        _logs.Entries.Should().AllSatisfy(entry =>
        {
            entry.Message.Should().NotContain(Address);
            (entry.Exception?.Message ?? string.Empty).Should().NotContain(Address);
        });
        return warning;
    }

    // The controller logs under its own type (the routed subclass).
    private List<LogEntry> ControllerEntries() =>
        [.. _logs.Entries.Where(entry => entry.Category.EndsWith(nameof(RoutedTwoFactorController), StringComparison.Ordinal))];

    private sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

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
                entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
        }
    }
}
