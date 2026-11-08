using AwesomeAssertions;
using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Domain.Tests.Auth;

/// <summary>
/// <see cref="PasskeyCredential"/> (ADR-139): the signature counter must advance on a counting
/// authenticator, zero on both sides is allowed (synced passkeys), and a refused use leaves the row
/// as it was; plus the name rules.
/// </summary>
public sealed class PasskeyCredentialTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static PasskeyCredential Create(uint signCount = 0, string? name = null) =>
        PasskeyCredential.Create(7, [1, 2, 3], [4, 5, 6], signCount, ["internal", "hybrid"], name, Now).Value!;

    [Theory]
    [InlineData(5u, 3u)]
    [InlineData(5u, 5u)]
    [InlineData(1u, 0u)]
    public void RecordUse_ACounterThatDoesNotAdvance_IsRefusedAndTheRowIsUnchanged(uint stored, uint presented)
    {
        var credential = Create(stored);

        var result = credential.RecordUse(presented, Now.AddMinutes(1));

        result.IsFailure.Should().BeTrue();
        credential.SignCount.Should().Be(stored);
        credential.LastUsedOn.Should().BeNull();
    }

    [Fact]
    public void RecordUse_ZeroOnBothSides_IsAllowed()
    {
        var credential = Create(0);

        credential.RecordUse(0, Now.AddMinutes(1)).IsSuccess.Should().BeTrue();
        credential.RecordUse(0, Now.AddMinutes(2)).IsSuccess.Should().BeTrue();
        credential.LastUsedOn.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void RecordUse_AnAdvancingCounter_IsRecorded()
    {
        var credential = Create(5);

        credential.RecordUse(6, Now.AddMinutes(1)).IsSuccess.Should().BeTrue();

        credential.SignCount.Should().Be(6);
        credential.LastUsedOn.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void Create_WithABlankName_UsesTheDefaultLabel_AndJoinsTheTransports()
    {
        var credential = Create(name: "  ");

        credential.Name.Should().Be(PasskeyCredential.DefaultName);
        credential.Transports.Should().Be("internal,hybrid");
        credential.UserId.Should().Be(7);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_ToABlankName_IsRefused(string name)
    {
        var credential = Create(name: "Phone");

        credential.Rename(name).IsFailure.Should().BeTrue();
        credential.Name.Should().Be("Phone");
    }

    [Fact]
    public void Rename_TrimsTheNewName()
    {
        var credential = Create(name: "Phone");

        credential.Rename("  Work laptop ").IsSuccess.Should().BeTrue();

        credential.Name.Should().Be("Work laptop");
    }
}
