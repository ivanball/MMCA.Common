using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using MMCA.Common.Application.Webhooks.Delivery;

namespace MMCA.Common.Application.Tests.Webhooks;

/// <summary>
/// <see cref="WebhookSignature"/> (rules 35.14, 35.32): the Standard Webhooks secret format and the
/// <c>v1,</c> HMAC-SHA256 signature over <c>id.timestamp.body</c>. Expected values are computed here
/// with <see cref="HMACSHA256"/> directly, never through the type under test.
/// </summary>
public sealed class WebhookSignatureTests
{
    private const string WebhookId = "6f1c1a3e-6a4e-4f39-9d0b-2a7c3b9b8e11";
    private const long Timestamp = 1_794_400_000;
    private const string Body = """{"type":"ticket.opened","timestamp":"2026-11-12T09:00:00.0000000Z","data":{"id":7}}""";

    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    [Fact]
    public void GenerateSecret_HasPrefixAnd32Bytes()
    {
        var secret = WebhookSignature.GenerateSecret();

        secret.Should().StartWith(WebhookSignature.SecretPrefix);
        WebhookSignature.SecretPrefix.Should().Be("whsec_");
        Convert.FromBase64String(secret[WebhookSignature.SecretPrefix.Length..]).Should().HaveCount(32);
    }

    [Fact]
    public void GenerateSecret_IsRandom() => WebhookSignature.GenerateSecret().Should().NotBe(WebhookSignature.GenerateSecret());

    [Fact]
    public void DecodeSecret_ReturnsTheBytesAfterThePrefix()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var secret = WebhookSignature.SecretPrefix + Convert.ToBase64String(key);

        var decoded = WebhookSignature.DecodeSecret(secret);

        decoded.IsSuccess.Should().BeTrue();
        decoded.Value.Should().Equal(key);
    }

    [Fact]
    public void DecodeSecret_RoundTripsAGeneratedSecret()
    {
        var secret = WebhookSignature.GenerateSecret();

        WebhookSignature.DecodeSecret(secret).Value.Should().Equal(Convert.FromBase64String(secret[6..]));
    }

    [Fact]
    public void DecodeSecret_WithoutPrefix_Fails()
    {
        var bare = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        WebhookSignature.DecodeSecret(bare).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void DecodeSecret_WithInvalidBase64AfterPrefix_Fails() => WebhookSignature.DecodeSecret(WebhookSignature.SecretPrefix + "not*base64!").IsFailure.Should().BeTrue();

    [Fact]
    public void Compute_EqualsIndependentHmacOfIdTimestampBody()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var signedContent = Encoding.UTF8.GetBytes($"{WebhookId}.{Timestamp}.{Body}");
        var expected = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, signedContent));

        WebhookSignature.Compute(WebhookId, Timestamp, Body, key).Should().Be(expected);
    }

    [Fact]
    public void Compute_IsDeterministic_AndKeyed()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var otherKey = RandomNumberGenerator.GetBytes(32);

        WebhookSignature.Compute(WebhookId, Timestamp, Body, key)
            .Should().Be(WebhookSignature.Compute(WebhookId, Timestamp, Body, key));
        WebhookSignature.Compute(WebhookId, Timestamp, Body, key)
            .Should().NotBe(WebhookSignature.Compute(WebhookId, Timestamp, Body, otherKey));
    }

    [Fact]
    public void HeaderNames_AreTheStandardWebhooksNames()
    {
        WebhookSignature.IdHeader.Should().Be("webhook-id");
        WebhookSignature.TimestampHeader.Should().Be("webhook-timestamp");
        WebhookSignature.SignatureHeader.Should().Be("webhook-signature");
    }

    [Fact]
    public void Verify_AcceptsOwnSignature()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var signature = WebhookSignature.Compute(WebhookId, Timestamp, Body, key);

        WebhookSignature.Verify(WebhookId, Timestamp, Body, signature, key, Timestamp, Tolerance).Should().BeTrue();
    }

    [Fact]
    public void Verify_RejectsTamperedBody()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var signature = WebhookSignature.Compute(WebhookId, Timestamp, Body, key);

        WebhookSignature.Verify(WebhookId, Timestamp, Body.Replace("7", "8", StringComparison.Ordinal), signature, key, Timestamp, Tolerance)
            .Should().BeFalse();
    }

    [Fact]
    public void Verify_RejectsTamperedIdOrTimestamp()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var signature = WebhookSignature.Compute(WebhookId, Timestamp, Body, key);

        WebhookSignature.Verify("another-id", Timestamp, Body, signature, key, Timestamp, Tolerance).Should().BeFalse();
        WebhookSignature.Verify(WebhookId, Timestamp + 1, Body, signature, key, Timestamp, Tolerance).Should().BeFalse();
    }

    [Fact]
    public void Verify_RejectsTheWrongKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var signature = WebhookSignature.Compute(WebhookId, Timestamp, Body, key);

        WebhookSignature.Verify(WebhookId, Timestamp, Body, signature, RandomNumberGenerator.GetBytes(32), Timestamp, Tolerance)
            .Should().BeFalse();
    }

    [Fact]
    public void Verify_RejectsTimestampOutsideTolerance()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var signature = WebhookSignature.Compute(WebhookId, Timestamp, Body, key);
        var toleranceSeconds = (long)Tolerance.TotalSeconds;

        WebhookSignature.Verify(WebhookId, Timestamp, Body, signature, key, Timestamp + toleranceSeconds + 1, Tolerance)
            .Should().BeFalse("a message older than the tolerance is a replay");
        WebhookSignature.Verify(WebhookId, Timestamp, Body, signature, key, Timestamp - toleranceSeconds - 1, Tolerance)
            .Should().BeFalse("a timestamp too far in the future is refused too");
    }

    [Fact]
    public void Verify_AcceptsTimestampExactlyAtTolerance()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var signature = WebhookSignature.Compute(WebhookId, Timestamp, Body, key);
        var toleranceSeconds = (long)Tolerance.TotalSeconds;

        WebhookSignature.Verify(WebhookId, Timestamp, Body, signature, key, Timestamp + toleranceSeconds, Tolerance).Should().BeTrue();
        WebhookSignature.Verify(WebhookId, Timestamp, Body, signature, key, Timestamp - toleranceSeconds, Tolerance).Should().BeTrue();
    }

    [Fact]
    public void Verify_AcceptsWhenSecondOfTwoSignaturesMatches()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var stale = WebhookSignature.Compute(WebhookId, Timestamp, Body, RandomNumberGenerator.GetBytes(32));
        var header = stale + " " + WebhookSignature.Compute(WebhookId, Timestamp, Body, key);

        WebhookSignature.Verify(WebhookId, Timestamp, Body, header, key, Timestamp, Tolerance).Should().BeTrue();
    }

    [Fact]
    public void Verify_IgnoresEntriesWithoutTheV1Prefix()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var signature = WebhookSignature.Compute(WebhookId, Timestamp, Body, key);
        var otherVersion = "v2," + signature["v1,".Length..];

        WebhookSignature.Verify(WebhookId, Timestamp, Body, otherVersion, key, Timestamp, Tolerance).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v1,")]
    [InlineData("v1,not-base64")]
    public void Verify_RejectsEmptyOrMalformedHeaders(string header)
    {
        var key = RandomNumberGenerator.GetBytes(32);

        WebhookSignature.Verify(WebhookId, Timestamp, Body, header, key, Timestamp, Tolerance).Should().BeFalse();
    }
}
