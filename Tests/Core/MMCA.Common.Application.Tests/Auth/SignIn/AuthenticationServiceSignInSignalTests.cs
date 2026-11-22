using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.SignIn;

/// <summary>
/// Pins the password sign-in outcome signal (security review 2026-11-21, item A4): every
/// <c>LoginAsync</c> call adds exactly one measurement to the <c>mmca.auth.signin</c> counter on the
/// <c>MMCA.Common.Auth</c> meter, tagged only with <c>outcome</c>; every failed outcome writes one Warning
/// carrying the outcome and the first 12 hex characters of the SHA-256 of the normalized address; and no
/// log line or tag ever carries the address itself.
/// </summary>
public sealed class AuthenticationServiceSignInSignalTests
{
    // Mixed case and padding so a log line that echoes the raw input, or hashes it un-normalized, is caught.
    private const string RequestEmail = "  Ada.Lovelace@Example.COM ";
    private const string NormalizedEmail = "ada.lovelace@example.com";
    private const UserIdentifierType UserId = 41;

    public static TheoryData<string, string> Outcomes => new()
    {
        { "unknown-address", "invalid_credentials" },
        { "wrong-password", "invalid_credentials" },
        { "no-password-credential", "invalid_credentials" },
        { "locked-out", "locked_out" },
        { "second-factor-missing", "second_factor_required" },
        { "second-factor-wrong", "second_factor_failed" },
        { "email-not-confirmed", "email_not_confirmed" },
        { "app-gate", "rejected" },
        { "success", "success" },
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task LoginAsync_CountsEachOutcomeExactlyOnceWithOnlyTheOutcomeTag(string scenario, string expectedOutcome)
    {
        var harness = Arrange(scenario, out var request);
        using var recorder = new SignInCounterRecorder();

        await harness.Sut.LoginAsync(request, ipAddress: "198.51.100.4", userAgent: "agent");

        var measurement = recorder.Measurements.Should().ContainSingle(
            "one LoginAsync call is one attempt, whatever its outcome").Subject;
        measurement.Value.Should().Be(1);
        measurement.Tags.Should().ContainSingle("the counter carries exactly one tag")
            .Which.Should().Be(new KeyValuePair<string, string?>("outcome", expectedOutcome));
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task LoginAsync_NeverPutsTheAddressInALogLineOrATag(string scenario, string expectedOutcome)
    {
        _ = expectedOutcome;
        var harness = Arrange(scenario, out var request);
        using var recorder = new SignInCounterRecorder();

        await harness.Sut.LoginAsync(request, ipAddress: "198.51.100.4", userAgent: "agent");

        foreach (var (_, message) in harness.Logger.Lines)
        {
            message.Should().NotContainEquivalentOf("lovelace", "no log line may carry the address");
            message.Should().NotContainEquivalentOf("example.com");
        }

        foreach (var tag in recorder.Measurements.SelectMany(m => m.Tags))
        {
            (tag.Value ?? string.Empty).Should().NotContainEquivalentOf("lovelace");
            (tag.Value ?? string.Empty).Should().NotContain("198.51.100.4");
            (tag.Value ?? string.Empty).Should().NotBe(UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task LoginAsync_LogsOneWarningPerFailedOutcomeWithTheOutcomeAndTheTruncatedAddressHash(
        string scenario,
        string expectedOutcome)
    {
        var harness = Arrange(scenario, out var request);

        await harness.Sut.LoginAsync(request);

        var warnings = harness.Logger.Lines.Where(l => l.Level == LogLevel.Warning).ToList();
        if (expectedOutcome == "success")
        {
            warnings.Should().BeEmpty("only a failed outcome is logged as a Warning");
            return;
        }

        var warning = warnings.Should().ContainSingle("each failed outcome writes exactly one Warning").Subject;
        warning.Message.Should().Contain(expectedOutcome);
        warning.Message.Should().ContainEquivalentOf(
            ExpectedAddressHash(),
            "the line carries the first 12 hex characters of the SHA-256 of the normalized address");
        warning.Message.Should().NotContainEquivalentOf(
            FullAddressHash(),
            "only a truncated prefix of the digest may be logged");
    }

    // -- Helpers --
    private static string FullAddressHash() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizedEmail)));

    private static string ExpectedAddressHash() => FullAddressHash()[..12];

    private static SignInHarness<SignInTestUser> Arrange(string scenario, out LoginRequest request)
    {
        request = new LoginRequest(RequestEmail, "pw");
        var twoFactor = new Mock<ITwoFactorAuthenticator>();
        twoFactor
            .Setup(x => x.ChallengeAsync(It.IsAny<UserIdentifierType>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(TwoFactorOutcome.NotEnrolled));

        var harness = new SignInHarness<SignInTestUser>(
            twoFactor.Object,
            requireConfirmedEmail: string.Equals(scenario, "email-not-confirmed", StringComparison.Ordinal));
        var user = new SignInTestUser { Id = UserId };
        harness.Arrange(user);

        switch (scenario)
        {
            case "unknown-address":
                harness.Sut.UntrackedUser = null;
                break;
            case "wrong-password":
                harness.PasswordHasher
                    .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
                    .Returns(false);
                break;
            case "no-password-credential":
                user.PasswordHash = [];
                user.PasswordSalt = [];
                break;
            case "locked-out":
                harness.LoginProtection
                    .Setup(x => x.CheckLockoutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Failure(Error.TooManyRequests("Auth.TooManyAttempts", "Too many failed login attempts.")));
                break;
            case "second-factor-missing":
                twoFactor
                    .Setup(x => x.ChallengeAsync(UserId, null, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Failure<TwoFactorOutcome>(TwoFactorErrors.TwoFactorRequired()));
                break;
            case "second-factor-wrong":
                request = request with { TwoFactorCode = "000000" };
                twoFactor
                    .Setup(x => x.ChallengeAsync(UserId, "000000", It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Result.Failure<TwoFactorOutcome>(TwoFactorErrors.TwoFactorInvalid()));
                break;
            case "email-not-confirmed":
                user.Confirmed = false;
                break;
            case "app-gate":
                harness.Sut.LoginCandidateResult = Result.Failure(
                    Error.Unauthorized("Auth.AccountDeactivated", "Account is deactivated."));
                break;
            case "success":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario.");
        }

        return harness;
    }

    /// <summary>
    /// Listens to <c>mmca.auth.signin</c> on the <c>MMCA.Common.Auth</c> meter and keeps only the
    /// measurements recorded inside the test that created it: the meter is process-wide and other test
    /// classes sign in concurrently, so the recorder marks its own async flow and ignores the rest.
    /// </summary>
    private sealed class SignInCounterRecorder : IDisposable
    {
        private static readonly AsyncLocal<SignInCounterRecorder?> Current = new();

        private readonly MeterListener _listener = new();
        private readonly List<(long Value, List<KeyValuePair<string, string?>> Tags)> _measurements = [];
        private readonly Lock _gate = new();

        public SignInCounterRecorder()
        {
            Current.Value = this;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "MMCA.Common.Auth" && instrument.Name == "mmca.auth.signin")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                if (!ReferenceEquals(Current.Value, this))
                {
                    return;
                }

                var copied = new List<KeyValuePair<string, string?>>();
                foreach (var tag in tags)
                {
                    copied.Add(new KeyValuePair<string, string?>(tag.Key, tag.Value?.ToString()));
                }

                lock (_gate)
                {
                    _measurements.Add((value, copied));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<(long Value, List<KeyValuePair<string, string?>> Tags)> Measurements
        {
            get
            {
                lock (_gate)
                {
                    return [.. _measurements];
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
