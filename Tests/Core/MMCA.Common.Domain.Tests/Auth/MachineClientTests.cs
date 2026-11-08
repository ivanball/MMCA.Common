using AwesomeAssertions;
using MMCA.Common.Domain.Auth.MachineClients;

namespace MMCA.Common.Domain.Tests.Auth;

/// <summary>
/// The <see cref="MachineClient"/> row (ADR-140): which secrets it accepts and when, and how rotation,
/// promotion and revocation change that.
/// </summary>
public sealed class MachineClientTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Secrets_AreRandom_AndOnlyTheirHashIsKept()
    {
        var first = MachineClientSecret.Generate();
        var second = MachineClientSecret.Generate();

        first.Should().NotBe(second);
        first.Should().MatchRegex("^[A-Za-z0-9_-]{43}$", "32 bytes base64url-encoded without padding");
        MachineClientSecret.Hash(first).Should().HaveCount(MachineClientSecret.HashByteLength);
    }

    [Fact]
    public void Accepts_ThePrimarySecretOnly()
    {
        var (client, secret) = NewClient();

        client.Accepts(secret, Now).Should().BeTrue();
        client.Accepts(secret + "x", Now).Should().BeFalse();
        client.Accepts(null, Now).Should().BeFalse();
    }

    [Fact]
    public void AnExpiredSecret_IsRefused()
    {
        var (client, secret) = NewClient(expiresOn: Now.AddDays(30));

        client.Accepts(secret, Now.AddDays(29)).Should().BeTrue();
        client.Accepts(secret, Now.AddDays(30)).Should().BeFalse();
    }

    [Fact]
    public void Create_RefusesAnExpiryInThePast_AndABlankName()
    {
        MachineClient.Create("Sync", 1, MachineClientSecret.Hash("s"), Now.AddMinutes(-1), Now).Errors
            .Should().ContainSingle(e => e.Code == "MachineClients.SecretExpiryInvalid");
        MachineClient.Create("  ", 1, MachineClientSecret.Hash("s"), null, Now).Errors
            .Should().ContainSingle(e => e.Code == "MachineClients.NameInvalid");
    }

    [Fact]
    public void RotateThenPromote_SwapsWhichSecretWorks()
    {
        var (client, primary) = NewClient();
        var secondary = MachineClientSecret.Generate();

        client.Rotate(MachineClientSecret.Hash(secondary)).IsSuccess.Should().BeTrue();
        client.Accepts(primary, Now).Should().BeTrue();
        client.Accepts(secondary, Now).Should().BeTrue();

        client.Promote().IsSuccess.Should().BeTrue();
        client.Accepts(primary, Now).Should().BeFalse();
        client.Accepts(secondary, Now).Should().BeTrue();
        client.SecondarySecretHash.Should().BeNull();
    }

    [Fact]
    public void Promote_WithNoRotatedSecret_IsRefused()
    {
        var (client, _) = NewClient();

        client.Promote().Errors.Should().ContainSingle(e => e.Code == "MachineClients.NoSecondarySecret");
    }

    [Fact]
    public void Revoke_RefusesEverySecret_KeepsTheFirstInstant_AndBlocksRotation()
    {
        var (client, secret) = NewClient();

        client.Revoke(Now);
        client.Revoke(Now.AddHours(1));

        client.RevokedOn.Should().Be(Now);
        client.Accepts(secret, Now).Should().BeFalse();
        client.Rotate(MachineClientSecret.Hash("next")).Errors.Should().ContainSingle(e => e.Code == "MachineClients.Revoked");
    }

    private static (MachineClient Client, string Secret) NewClient(DateTime? expiresOn = null)
    {
        var secret = MachineClientSecret.Generate();
        var client = MachineClient.Create("Nightly sync", 42, MachineClientSecret.Hash(secret), expiresOn, Now).Value!;
        return (client, secret);
    }
}
