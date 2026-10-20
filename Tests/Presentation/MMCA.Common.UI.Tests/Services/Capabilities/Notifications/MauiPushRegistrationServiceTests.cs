using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.UI.Maui.Capabilities.Notifications;
using MMCA.Common.UI.Services.Capabilities.DeviceStorage;
using MMCA.Common.UI.Services.Capabilities.Notifications;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Capabilities.Notifications;

/// <summary>
/// Tests for <see cref="MauiPushRegistrationService"/> (ADR-044). The class ships in the MAUI-TFM
/// package, which no net10.0 test project can reference, but it uses no MAUI API, so the test
/// project compiles the one source file directly (see the linked <c>Compile</c> item in the csproj).
/// </summary>
public sealed class MauiPushRegistrationServiceTests : IDisposable
{
    private const string InstallationIdKey = "mmca.push.installationId";
    private const string TakenId = "0123456789abcdef0123456789abcdef";

    private readonly InMemoryDevicePreferences _preferences = new();
    private readonly RecordingHandler _handler = new();
    private readonly MauiPushRegistrationService _sut;

    public MauiPushRegistrationServiceTests()
    {
        var tokenProvider = new Mock<IPushDeviceTokenProvider>();
        tokenProvider
            .Setup(x => x.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PushDeviceToken("fcmv1", "fcm-registration-token"));
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(x => x.CreateClient("APIClient"))
            .Returns(() => new HttpClient(_handler, disposeHandler: false) { BaseAddress = new Uri("https://api.test/") });

        _sut = new MauiPushRegistrationService(
            tokenProvider.Object,
            httpClientFactory.Object,
            _preferences,
            NullLogger<MauiPushRegistrationService>.Instance);
    }

    // L176 review (R1): the server answers 409 when the persisted installation id belongs to another
    // user's device. The id is never rotated otherwise, so without a fresh id this device could never
    // register again.
    [Fact]
    public async Task RegisterAsync_WhenTheServerReportsAConflict_MintsAPersistedNewIdAndRetriesOnce()
    {
        await _preferences.SetAsync(InstallationIdKey, TakenId, TestContext.Current.CancellationToken);
        _handler.Responses.Enqueue(HttpStatusCode.Conflict);
        _handler.Responses.Enqueue(HttpStatusCode.NoContent);

        var registered = await _sut.RegisterAsync(TestContext.Current.CancellationToken);

        registered.Should().BeTrue();
        _handler.InstallationIds.Should().HaveCount(2);
        _handler.InstallationIds[0].Should().Be(TakenId);
        var minted = await _preferences.GetAsync<string?>(InstallationIdKey, null, TestContext.Current.CancellationToken);
        minted.Should().NotBeNullOrWhiteSpace().And.NotBe(TakenId, "the conflicting id must be replaced");
        _handler.InstallationIds[1].Should().Be(minted, "the retry uses the id that was persisted");
    }

    [Fact]
    public async Task RegisterAsync_WhenTheRetryAlsoConflicts_GivesUpAfterOneRetry()
    {
        await _preferences.SetAsync(InstallationIdKey, TakenId, TestContext.Current.CancellationToken);
        _handler.Responses.Enqueue(HttpStatusCode.Conflict);
        _handler.Responses.Enqueue(HttpStatusCode.Conflict);
        _handler.Responses.Enqueue(HttpStatusCode.NoContent);

        var registered = await _sut.RegisterAsync(TestContext.Current.CancellationToken);

        registered.Should().BeFalse();
        _handler.InstallationIds.Should().HaveCount(2, "the client retries exactly once");
    }

    [Fact]
    public async Task RegisterAsync_WhenTheServerRejectsForAnotherReason_KeepsTheInstallationId()
    {
        await _preferences.SetAsync(InstallationIdKey, TakenId, TestContext.Current.CancellationToken);
        _handler.Responses.Enqueue(HttpStatusCode.BadRequest);

        var registered = await _sut.RegisterAsync(TestContext.Current.CancellationToken);

        registered.Should().BeFalse();
        _handler.InstallationIds.Should().ContainSingle();
        var stored = await _preferences.GetAsync<string?>(InstallationIdKey, null, TestContext.Current.CancellationToken);
        stored.Should().Be(TakenId);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _handler.Dispose();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Queue<HttpStatusCode> Responses { get; } = new();

        public List<string?> InstallationIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            InstallationIds.Add(document.RootElement.GetProperty("installationId").GetString());
            return new HttpResponseMessage(Responses.Count > 0 ? Responses.Dequeue() : HttpStatusCode.InternalServerError);
        }
    }
}
