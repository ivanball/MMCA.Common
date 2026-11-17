using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Application.Users.UseCases.ExportUserData;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Testing.Support;

namespace MMCA.Common.Application.Tests.Users.UseCases.ExportUserData;

/// <summary>
/// Exercises the framework's sign-in sessions export section: every row the user holds (revoked and
/// expired ones included) with its timestamps, client and device fields, another user's rows left out,
/// and no token hash anywhere in the exported shape.
/// </summary>
public sealed class RefreshSessionUserDataExportSectionTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ExportAsync_ExportsEveryRowOfTheUser_WithTimestampsClientAndDeviceFields()
    {
        var store = new InMemoryRefreshSessionStore();
        var live = Seed(store, userId: 1, "t-live", Now.AddHours(-1), Now.AddDays(7));
        live.RecordDevice(new DeviceMetadata("install-1", "Ana's phone", "Pixel 9", "Google", "Android 16", "Phone", "Physical"));
        var revoked = Seed(store, userId: 1, "t-revoked", Now.AddDays(-3), Now.AddDays(4));
        revoked.Revoke(Now.AddDays(-2), RefreshSession.ReasonSignedOut);
        Seed(store, userId: 2, "t-other", Now.AddHours(-2), Now.AddDays(7));
        var sut = new RefreshSessionUserDataExportSection(store);

        var result = await sut.ExportAsync(1, TestContext.Current.CancellationToken);

        result.Available.Should().BeTrue();
        result.SectionName.Should().Be(RefreshSessionUserDataExportSection.Name);
        var sessions = result.Data.Should().BeAssignableTo<IReadOnlyList<UserSessionExportDTO>>().Subject;
        sessions.Should().HaveCount(2, "a revoked row still holds the data captured at issue time; another user's row is not this subject's");
        sessions.Should().ContainSingle(s => s.RevokedAt == Now.AddDays(-2)).Which.RevocationReason.Should().Be(RefreshSession.ReasonSignedOut);
        var device = sessions.Should().ContainSingle(s => s.RevokedAt == null).Subject;
        device.CreatedAt.Should().Be(Now.AddHours(-1));
        device.ExpiresAt.Should().Be(Now.AddDays(7));
        device.IpAddress.Should().Be("203.0.113.7");
        device.UserAgent.Should().Be("Mozilla/5.0 (test)");
        device.Should().BeEquivalentTo(new
        {
            DeviceId = "install-1",
            DeviceName = "Ana's phone",
            DeviceModel = "Pixel 9",
            DeviceManufacturer = "Google",
            DevicePlatform = "Android 16",
            DeviceFormFactor = "Phone",
            DeviceType = "Physical",
        });
    }

    [Fact]
    public async Task ExportAsync_ForAUserWithNoSessions_IsCompleteAndEmpty()
    {
        var sut = new RefreshSessionUserDataExportSection(new InMemoryRefreshSessionStore());

        var result = await sut.ExportAsync(1, TestContext.Current.CancellationToken);

        result.Available.Should().BeTrue("holding nothing is a truthful answer, not an unknown one");
        result.Data.Should().BeAssignableTo<IReadOnlyList<UserSessionExportDTO>>().Which.Should().BeEmpty();
    }

    [Fact]
    public void ExportedShape_CarriesNoTokenHashOrIdentifier()
    {
        var names = typeof(UserSessionExportDTO)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        names.Should().NotContain(n => n.Contains("Hash", StringComparison.OrdinalIgnoreCase) || n.Contains("Token", StringComparison.OrdinalIgnoreCase),
            "a token hash is credentials material, never portable personal data");
    }

    private static RefreshSession Seed(
        InMemoryRefreshSessionStore store,
        UserIdentifierType userId,
        string token,
        DateTime createdAt,
        DateTime expiresAt)
    {
        var session = RefreshSession.Create(userId, token, createdAt, expiresAt, "203.0.113.7", "Mozilla/5.0 (test)").Value!;
        store.Seed(session);
        return session;
    }
}
