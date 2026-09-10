using AwesomeAssertions;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Auth.TwoFactor;
using MMCA.Common.Infrastructure.Auth.TwoFactor;
using OtpNet;

namespace MMCA.Common.Infrastructure.Tests.Auth;

/// <summary>
/// Verifies the TOTP second factor's cryptography: secrets an authenticator app can key from, codes
/// that verify inside the configured skew window and nowhere outside it, and recovery codes that are
/// stored as hashes and matched exactly once.
/// </summary>
public sealed class TotpTwoFactorServiceTests
{
    // ── Secret and provisioning URI ──
    [Fact]
    public void GenerateSecret_ProducesDistinctBase32SecretsOfTheConfiguredLength()
    {
        var sut = CreateSut(secretByteLength: 20);

        string first = sut.GenerateSecret();
        string second = sut.GenerateSecret();

        first.Should().NotBe(second, "every enrollment gets its own secret");
        Base32Encoding.ToBytes(first).Should().HaveCount(20);
    }

    [Fact]
    public void BuildProvisioningUri_CarriesTheIssuerLabelAndEveryParameterAnAuthenticatorReads()
    {
        var sut = CreateSut(issuer: "Helpdesk", digits: 6, periodSeconds: 30);
        string secret = sut.GenerateSecret();

        string uri = sut.BuildProvisioningUri(secret, "user@example.com");

        uri.Should().StartWith("otpauth://totp/Helpdesk:user%40example.com?");
        uri.Should().Contain("secret=" + secret);
        uri.Should().Contain("issuer=Helpdesk");
        uri.Should().Contain("algorithm=SHA1").And.Contain("digits=6").And.Contain("period=30");
    }

    // ── Verification and the skew window ──
    [Fact]
    public void VerifyCode_WithTheCurrentCode_Succeeds()
    {
        var sut = CreateSut();
        string secret = sut.GenerateSecret();
        string code = CodeAt(secret, DateTime.UtcNow);

        sut.VerifyCode(secret, code).Should().BeTrue();
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public void VerifyCode_OneStepOutOfSync_SucceedsWithTheDefaultWindow(int offsetSeconds)
    {
        // The default window of one step exists so a phone whose clock drifts by up to a step still
        // signs in; without it the feature is a support burden rather than a security control.
        var sut = CreateSut(verificationWindowSteps: 1);
        string secret = sut.GenerateSecret();
        string code = CodeAt(secret, DateTime.UtcNow.AddSeconds(offsetSeconds));

        sut.VerifyCode(secret, code).Should().BeTrue();
    }

    [Theory]
    [InlineData(-90)]
    [InlineData(90)]
    public void VerifyCode_ThreeStepsOutOfSync_FailsWithTheDefaultWindow(int offsetSeconds)
    {
        var sut = CreateSut(verificationWindowSteps: 1);
        string secret = sut.GenerateSecret();
        string code = CodeAt(secret, DateTime.UtcNow.AddSeconds(offsetSeconds));

        sut.VerifyCode(secret, code).Should().BeFalse("the window is what bounds how many codes are live at once");
    }

    [Fact]
    public void VerifyCode_WithAWindowOfZero_RejectsTheNeighbouringStep()
    {
        var sut = CreateSut(verificationWindowSteps: 0);
        string secret = sut.GenerateSecret();

        sut.VerifyCode(secret, CodeAt(secret, DateTime.UtcNow)).Should().BeTrue();
        sut.VerifyCode(secret, CodeAt(secret, DateTime.UtcNow.AddSeconds(-30))).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("000000")]
    [InlineData("not-a-code")]
    public void VerifyCode_WithAMissingOrWrongCode_Fails(string? code)
    {
        var sut = CreateSut();
        string secret = sut.GenerateSecret();

        // "000000" is astronomically unlikely to be the live code and stands in for a wrong guess.
        sut.VerifyCode(secret, code).Should().BeFalse();
    }

    [Fact]
    public void VerifyCode_WithAnUnreadableStoredSecret_FailsInsteadOfThrowing()
    {
        // A corrupted secret is a data fault, and the login path must answer it as "does not verify"
        // rather than as an exception.
        var sut = CreateSut();

        sut.VerifyCode("not base32 at all!!", "123456").Should().BeFalse();
    }

    [Fact]
    public void VerifyCode_AcceptsACodeTypedWithSeparators()
    {
        var sut = CreateSut();
        string secret = sut.GenerateSecret();
        string code = CodeAt(secret, DateTime.UtcNow);

        sut.VerifyCode(secret, $" {code[..3]} {code[3..]} ").Should().BeTrue();
    }

    // ── Recovery codes ──
    [Fact]
    public void GenerateRecoveryCodes_ProducesTheConfiguredCountWithMatchingHashes()
    {
        var sut = CreateSut(recoveryCodeCount: 8);

        RecoveryCodeSet set = sut.GenerateRecoveryCodes();

        set.Codes.Should().HaveCount(8);
        set.Hashes.Should().HaveCount(8);
        set.Codes.Should().OnlyHaveUniqueItems();
        set.Hashes.Should().OnlyHaveUniqueItems();
        set.Hashes.Should().NotContain(hash => set.Codes.Contains(hash), "the stored value is never the code itself");

        for (int index = 0; index < set.Codes.Count; index++)
        {
            sut.HashRecoveryCode(set.Codes[index]).Should().Be(set.Hashes[index]);
        }
    }

    [Fact]
    public void TryMatchRecoveryCode_FindsTheMatchingHashAndOnlyThatOne()
    {
        var sut = CreateSut(recoveryCodeCount: 5);
        RecoveryCodeSet set = sut.GenerateRecoveryCodes();

        bool matched = sut.TryMatchRecoveryCode(set.Codes[3], set.Hashes, out string? hash);

        matched.Should().BeTrue();
        hash.Should().Be(set.Hashes[3]);
    }

    [Fact]
    public void TryMatchRecoveryCode_AfterTheHashIsRemoved_NoLongerMatches()
    {
        // This is what "single use" means at this layer: the service matches against whatever hashes
        // the store still holds, so spending a code is removing its hash.
        var sut = CreateSut(recoveryCodeCount: 3);
        RecoveryCodeSet set = sut.GenerateRecoveryCodes();
        List<string> remaining = [.. set.Hashes];
        remaining.Remove(set.Hashes[0]);

        sut.TryMatchRecoveryCode(set.Codes[0], remaining, out string? hash).Should().BeFalse();
        hash.Should().BeNull();
    }

    [Fact]
    public void TryMatchRecoveryCode_IsCaseAndSeparatorInsensitive()
    {
        var sut = CreateSut(recoveryCodeCount: 2);
        RecoveryCodeSet set = sut.GenerateRecoveryCodes();

#pragma warning disable CA1308 // Lower casing is the point: the service must normalize a code the user typed in either case.
        string typed = set.Codes[0].ToLowerInvariant().Insert(4, "-");
#pragma warning restore CA1308

        sut.TryMatchRecoveryCode(typed, set.Hashes, out string? hash).Should().BeTrue();
        hash.Should().Be(set.Hashes[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryMatchRecoveryCode_WithNoCode_Fails(string? code)
    {
        var sut = CreateSut(recoveryCodeCount: 2);
        RecoveryCodeSet set = sut.GenerateRecoveryCodes();

        sut.TryMatchRecoveryCode(code, set.Hashes, out string? hash).Should().BeFalse();
        hash.Should().BeNull();
    }

    [Fact]
    public void TryMatchRecoveryCode_AgainstAnEmptyList_Fails()
    {
        var sut = CreateSut();

        sut.TryMatchRecoveryCode("ANYTHING", [], out string? hash).Should().BeFalse();
        hash.Should().BeNull();
    }

    // ── Helpers ──
    private static TotpTwoFactorService CreateSut(
        string issuer = "MMCA",
        int digits = 6,
        int periodSeconds = 30,
        int verificationWindowSteps = 1,
        int recoveryCodeCount = 10,
        int secretByteLength = 20) =>
        new(Options.Create(new TwoFactorSettings
        {
            Issuer = issuer,
            Digits = digits,
            PeriodSeconds = periodSeconds,
            VerificationWindowSteps = verificationWindowSteps,
            RecoveryCodeCount = recoveryCodeCount,
            SecretByteLength = secretByteLength,
        }));

    /// <summary>Mints the code an authenticator app would show at a given instant.</summary>
    private static string CodeAt(string secret, DateTime utcInstant) =>
        new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(utcInstant);
}
