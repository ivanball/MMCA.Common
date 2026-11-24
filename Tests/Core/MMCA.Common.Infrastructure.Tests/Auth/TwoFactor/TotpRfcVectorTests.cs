using System.Text;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Auth.TwoFactor;

namespace MMCA.Common.Infrastructure.Tests.Auth.TwoFactor;

/// <summary>
/// Pins the internal TOTP and Base32 implementations to the published test vectors: RFC 4648
/// section 10 for Base32, RFC 4226 Appendix D for the HOTP truncation and RFC 6238 Appendix B for
/// the time step. Every expected value is copied from the RFC, never produced by the code under test.
/// </summary>
public sealed class TotpRfcVectorTests
{
    // RFC 6238 Appendix B and RFC 4226 Appendix D: the SHA-1 seed is the ASCII string below.
    private static readonly byte[] RfcSeed = Encoding.ASCII.GetBytes("12345678901234567890");

    // -- RFC 4648 section 10 --
    [Theory]
    [InlineData("f", "MY======")]
    [InlineData("fo", "MZXQ====")]
    [InlineData("foo", "MZXW6===")]
    [InlineData("foob", "MZXW6YQ=")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI======")]
    public void Base32_EncodesAndDecodesTheRfc4648Vectors(string plain, string encoded)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(plain);

        Base32.Encode(bytes).Should().Be(encoded);
        Base32.Decode(encoded).Should().Equal(bytes);
    }

    [Theory]
    [InlineData("MZXW6YTBOI", "foobar")]
    [InlineData("mzxw6ytboi======", "foobar")]
    [InlineData("MzXw6Yq", "foob")]
    public void Base32_Decode_ToleratesMissingPaddingAndLowerCase(string encoded, string plain) =>
        Base32.Decode(encoded).Should().Equal(Encoding.ASCII.GetBytes(plain));

    [Theory]
    [InlineData("MZXW 6YQ")]
    [InlineData("MZXW1YQ")]
    [InlineData("MZ=W6YQ")]
    [InlineData("MZXW6YQ!")]
    public void Base32_Decode_RejectsCharactersOutsideTheAlphabet(string encoded)
    {
        Action act = () => Base32.Decode(encoded);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Base32_Decode_OfPaddingAlone_IsEmpty() => Base32.Decode("========").Should().BeEmpty();

    // -- RFC 4226 Appendix D (HOTP, six digits, counter = time step) --
    [Theory]
    [InlineData(0, "755224")]
    [InlineData(1, "287082")]
    [InlineData(2, "359152")]
    [InlineData(3, "969429")]
    [InlineData(4, "338314")]
    [InlineData(5, "254676")]
    [InlineData(6, "287922")]
    [InlineData(7, "162583")]
    [InlineData(8, "399871")]
    [InlineData(9, "520489")]
    public void Totp_MatchesTheRfc4226HotpVectors(long counter, string expected) =>
        Totp.Compute(RfcSeed, DateTime.UnixEpoch.AddSeconds(counter * 30), periodSeconds: 30, digits: 6)
            .Should().Be(expected);

    // -- RFC 6238 Appendix B (SHA-1, eight digits, thirty-second step) --
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Totp_MatchesTheRfc6238Vectors(long unixSeconds, string expected)
    {
        DateTime instant = DateTime.UnixEpoch.AddSeconds(unixSeconds);

        Totp.Compute(RfcSeed, instant, periodSeconds: 30, digits: 8).Should().Be(expected);
        Totp.Verify(RfcSeed, expected, instant, periodSeconds: 30, digits: 8, windowSteps: 0, out long step).Should().BeTrue();
        step.Should().Be(unixSeconds / 30);
    }

    // -- The verification window --
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Totp_Verify_ReportsTheStepThatMatchedInsideTheWindow(int offsetSteps)
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(1111111111);
        long currentStep = 1111111111 / 30;
        string code = Totp.Compute(RfcSeed, now.AddSeconds(offsetSteps * 30), periodSeconds: 30, digits: 6);

        Totp.Verify(RfcSeed, code, now, periodSeconds: 30, digits: 6, windowSteps: 1, out long step).Should().BeTrue();
        step.Should().Be(currentStep + offsetSteps);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public void Totp_Verify_RejectsAStepOutsideTheWindow(int offsetSteps)
    {
        DateTime now = DateTime.UnixEpoch.AddSeconds(1111111111);
        string code = Totp.Compute(RfcSeed, now.AddSeconds(offsetSteps * 30), periodSeconds: 30, digits: 6);

        Totp.Verify(RfcSeed, code, now, periodSeconds: 30, digits: 6, windowSteps: 1, out long step).Should().BeFalse();
        step.Should().Be(0);
    }

    [Fact]
    public void Totp_Verify_AtStepZero_StillAcceptsTheNextStep()
    {
        // RFC 4226 counter 1 is the step after step zero; there is no step before zero to try.
        Totp.Verify(RfcSeed, "287082", DateTime.UnixEpoch, periodSeconds: 30, digits: 6, windowSteps: 2, out long step)
            .Should().BeTrue();
        step.Should().Be(1);
    }

    [Theory]
    [InlineData("75522")]
    [InlineData("7552240")]
    [InlineData("ABCDEF")]
    public void Totp_Verify_RejectsACodeOfTheWrongShape(string code) =>
        Totp.Verify(RfcSeed, code, DateTime.UnixEpoch, periodSeconds: 30, digits: 6, windowSteps: 1, out _)
            .Should().BeFalse();
}
