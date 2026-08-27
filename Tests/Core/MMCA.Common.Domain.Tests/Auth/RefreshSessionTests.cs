using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Tests.Auth;

/// <summary>
/// The refresh-session record: hash-at-rest storage, the rotation chain, and revocation invariants
/// (BR-205/206).
/// </summary>
public sealed class RefreshSessionTests
{
    private static readonly DateTime Now = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_StoresTheHashAndNeverTheToken()
    {
        Result<RefreshSession> result = RefreshSession.Create(7, "plaintext-token", Now, Now.AddDays(7));

        result.IsSuccess.Should().BeTrue();
        var session = result.Value!;
        session.TokenHash.Should().NotBe("plaintext-token");
        session.TokenHash.Should().Be(RefreshSession.HashToken("plaintext-token"));
        session.UserId.Should().Be(7);
        session.CreatedAt.Should().Be(Now);
        session.ExpiresAt.Should().Be(Now.AddDays(7));
        session.IsRevoked.Should().BeFalse();
        session.IsActiveAt(Now).Should().BeTrue();
    }

    [Fact]
    public void Create_WithoutAToken_Fails()
    {
        Result<RefreshSession> result = RefreshSession.Create(7, "   ", Now, Now.AddDays(7));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "RefreshSession.TokenRequired");
    }

    [Fact]
    public void Create_WithAnExpiryThatIsNotInTheFuture_Fails()
    {
        Result<RefreshSession> result = RefreshSession.Create(7, "token", Now, Now);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "RefreshSession.ExpiryInPast");
    }

    [Fact]
    public void Create_TruncatesTheCapturedClientMetadataToItsColumnWidth()
    {
        var longAgent = new string('a', RefreshSession.UserAgentMaxLength + 50);

        var session = RefreshSession.Create(7, "token", Now, Now.AddDays(7), new string('1', 60), longAgent).Value!;

        session.IpAddress.Should().HaveLength(RefreshSession.IpAddressMaxLength);
        session.UserAgent.Should().HaveLength(RefreshSession.UserAgentMaxLength);
    }

    // The migration that carries existing tokens over reproduces this in T-SQL
    // (CONVERT(char(64), HASHBYTES('SHA2_256', CONVERT(varchar(max), Token)), 2)), so the encoding is
    // a contract: upper-case hex over the UTF-8 bytes, 64 characters wide.
    [Fact]
    public void HashToken_IsUpperCaseHexOfTheUtf8Sha256Digest()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("a-token")));

        var hash = RefreshSession.HashToken("a-token");

        hash.Should().Be(expected);
        hash.Should().HaveLength(RefreshSession.TokenHashLength);
        hash.Should().MatchRegex("^[0-9A-F]{64}$");
    }

    [Fact]
    public void HashToken_IsDeterministic()
    {
        RefreshSession.HashToken("same").Should().Be(
            RefreshSession.HashToken("same"),
            "reuse detection looks a presented token up by hash, so the digest cannot be salted");
        RefreshSession.HashToken("same").Should().NotBe(RefreshSession.HashToken("other"));
    }

    [Fact]
    public void Revoke_RecordsWhenWhyAndTheSuccessor()
    {
        var session = RefreshSession.Create(7, "token", Now, Now.AddDays(7)).Value!;
        var successorHash = RefreshSession.HashToken("successor");

        Result result = session.Revoke(Now.AddHours(1), RefreshSession.ReasonRotated, successorHash);

        result.IsSuccess.Should().BeTrue();
        session.IsRevoked.Should().BeTrue();
        session.RevokedAt.Should().Be(Now.AddHours(1));
        session.ReasonRevoked.Should().Be(RefreshSession.ReasonRotated);
        session.ReplacedByTokenHash.Should().Be(successorHash);
        session.IsActiveAt(Now.AddHours(2)).Should().BeFalse();
    }

    [Fact]
    public void Revoke_OnAnAlreadyRevokedSession_FailsAndKeepsTheFirstReason()
    {
        var session = RefreshSession.Create(7, "token", Now, Now.AddDays(7)).Value!;
        session.Revoke(Now.AddHours(1), RefreshSession.ReasonReuseDetected);

        Result second = session.Revoke(Now.AddHours(2), RefreshSession.ReasonSignedOut);

        second.IsFailure.Should().BeTrue();
        second.Errors.Should().ContainSingle(e => e.Code == "RefreshSession.AlreadyRevoked");
        session.ReasonRevoked.Should().Be(RefreshSession.ReasonReuseDetected, "the first revocation is the true one");
        session.RevokedAt.Should().Be(Now.AddHours(1));
    }

    [Fact]
    public void IsActiveAt_IsFalseOnceExpired()
    {
        var session = RefreshSession.Create(7, "token", Now, Now.AddDays(7)).Value!;

        session.IsActiveAt(Now.AddDays(7)).Should().BeFalse("expiry is exclusive: at ExpiresAt the session is done");
        session.IsActiveAt(Now.AddDays(6)).Should().BeTrue();
    }
}
