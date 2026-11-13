using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Webhooks;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Webhooks;
using Moq;

namespace MMCA.Common.Application.Tests.Webhooks;

/// <summary>
/// <see cref="WebhookSubscriptionService"/> (rules 35.11 to 35.17): tenant stamping and isolation,
/// the per-tenant limit, catalog checks, target validation, and the secret that is returned once and
/// stored only protected.
/// </summary>
public sealed class WebhookSubscriptionServiceTests
{
    private const string Tenant = "tenant-a";
    private const string Url = "https://hooks.example.com/inbound";

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly Mock<IWebhookSubscriptionStore> _store = new();
    private readonly Mock<IWebhookTargetValidator> _targetValidator = new();
    private readonly Mock<IWebhookSecretProtector> _protector = new();
    private readonly Mock<ITenantContext> _tenant = new();
    private readonly List<WebhookSubscription> _added = [];
    private WebhookSettings _settings = new() { Enabled = true, PreviousSecretGraceHours = 6, MaxSubscriptionsPerTenant = 3 };

    public WebhookSubscriptionServiceTests()
    {
        _tenant.SetupGet(t => t.TenantId).Returns(Tenant);
        _targetValidator.Setup(v => v.ValidateAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());
        _protector.Setup(p => p.Protect(It.IsAny<string>())).Returns((string secret) => "protected:" + secret);
        _store.Setup(s => s.CountAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _store.Setup(s => s.AddAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()))
            .Callback<WebhookSubscription, CancellationToken>((subscription, _) => _added.Add(subscription))
            .Returns(Task.CompletedTask);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public void IsEnabled_AndEventTypes_ComeFromSettingsAndCatalog()
    {
        var sut = CreateSut();

        sut.IsEnabled.Should().BeTrue();
        sut.EventTypes.Should().BeEquivalentTo("ticket.opened", "ticket.closed");

        _settings = new WebhookSettings { Enabled = false };
        CreateSut().IsEnabled.Should().BeFalse();
    }

    // -- Create (rules 35.11 to 35.14) --
    [Fact]
    public async Task Create_StampsTenantFromContext()
    {
        var result = await CreateSut().CreateAsync(Request(["ticket.opened"]), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        _added.Should().ContainSingle().Which.TenantId.Should().Be(Tenant);
        _store.Verify(s => s.CountAsync(Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_WithNoTenant_StampsNull()
    {
        _tenant.SetupGet(t => t.TenantId).Returns((string?)null);

        await CreateSut().CreateAsync(Request(["ticket.opened"]), TestContext.Current.CancellationToken);

        _added.Should().ContainSingle().Which.TenantId.Should().BeNull();
        _store.Verify(s => s.CountAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_ReturnsPlainSecretOnce_StoresProtected()
    {
        var result = await CreateSut().CreateAsync(Request(["ticket.opened"]), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        var created = result.Value!;
        var secret = created.Secret;
        secret.Should().StartWith("whsec_");
        Convert.FromBase64String(secret["whsec_".Length..]).Should().HaveCount(32);
        _protector.Verify(p => p.Protect(secret), Times.Once);
        var stored = _added.Should().ContainSingle().Which;
        stored.ProtectedSecret.Should().Be("protected:" + secret, "only the protected form is stored");
        stored.ProtectedSecret.Should().NotBe(secret);
        created.Subscription.Id.Should().Be(stored.Id);
        created.Subscription.Url.Should().Be(Url);
        created.Subscription.EventTypes.Should().Equal("ticket.opened");
        created.Subscription.IsEnabled.Should().BeTrue();
        Serialize(created.Subscription).Should().NotContain(secret).And.NotContain("protected:");
    }

    [Fact]
    public async Task Create_TwoSubscriptions_GetDifferentSecrets()
    {
        var sut = CreateSut();

        var first = await sut.CreateAsync(Request(["ticket.opened"]), TestContext.Current.CancellationToken);
        var second = await sut.CreateAsync(Request(["ticket.opened"]), TestContext.Current.CancellationToken);

        first.Value!.Secret.Should().NotBe(second.Value!.Secret);
    }

    [Fact]
    public async Task Create_UnknownEventType_Fails()
    {
        var result = await CreateSut().CreateAsync(Request(["ticket.opened", "ticket.deleted"]), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.EventTypeUnknown);
        result.Errors.Single().Type.Should().Be(ErrorType.Validation);
        _store.Verify(s => s.AddAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_WithNoEventTypesMember_FailsEventTypesRequired()
    {
        // A body with no eventTypes member deserializes EventTypes as null (ruling L2): a 400, not a 500.
        var result = await CreateSut().CreateAsync(
            new CreateWebhookSubscriptionRequest(Url, null, null!), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.EventTypesRequired);
        result.Errors.Single().Type.Should().Be(ErrorType.Validation);
        _store.Verify(s => s.AddAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_WithNoEventTypesMember_FailsEventTypesRequired()
    {
        var subscription = SeedOwn("protected:s");

        var result = await CreateSut().UpdateAsync(
            subscription.Id, new UpdateWebhookSubscriptionRequest(Url, null, null!), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.EventTypesRequired);
        result.Errors.Single().Type.Should().Be(ErrorType.Validation);
        subscription.GetEventTypes().Should().Equal("ticket.opened");
        _store.Verify(s => s.SaveAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_WildcardAccepted()
    {
        var result = await CreateSut().CreateAsync(Request(["*"]), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue("the wildcard is accepted although the catalog does not list it");
        _added.Should().ContainSingle().Which.EventTypes.Should().Be("*");
    }

    [Fact]
    public async Task Create_InvalidUrl_FailsUrlInvalid_WithoutAdding()
    {
        var result = await CreateSut().CreateAsync(
            new CreateWebhookSubscriptionRequest("/relative", null, ["ticket.opened"]), TestContext.Current.CancellationToken);

        result.Errors.Should().Contain(e => e.Code == WebhookErrorCodes.UrlInvalid);
        _store.Verify(s => s.AddAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_TargetRefused_FailsTargetNotAllowed()
    {
        _targetValidator.Setup(v => v.ValidateAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Validation(WebhookErrorCodes.TargetNotAllowed, "Resolves to a private address.")));

        var result = await CreateSut().CreateAsync(Request(["ticket.opened"]), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.TargetNotAllowed);
        _targetValidator.Verify(v => v.ValidateAsync(new Uri(Url), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.AddAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
        _protector.Verify(p => p.Protect(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Create_AtLimit_FailsLimitReached()
    {
        _store.Setup(s => s.CountAsync(Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(_settings.MaxSubscriptionsPerTenant);

        var result = await CreateSut().CreateAsync(Request(["ticket.opened"]), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.LimitReached);
        result.Errors.Single().Type.Should().Be(ErrorType.Conflict);
        _store.Verify(s => s.AddAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_OneBelowLimit_Succeeds()
    {
        _store.Setup(s => s.CountAsync(Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(_settings.MaxSubscriptionsPerTenant - 1);

        var result = await CreateSut().CreateAsync(Request(["ticket.opened"]), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
    }

    // -- Tenant isolation and not-found (rule 35.17) --
    [Fact]
    public async Task Find_OtherTenant_NotFound()
    {
        // The row belongs to tenant-b; the store returns it only for tenant-b, and the caller is tenant-a.
        var foreign = WebhookSubscription.Create(Url, null, ["ticket.opened"], "tenant-b", "protected:x").Value!;
        _store.Setup(s => s.FindAsync(foreign.Id, "tenant-b", It.IsAny<CancellationToken>())).ReturnsAsync(foreign);
        var sut = CreateSut();
        var ct = TestContext.Current.CancellationToken;

        (await sut.UpdateAsync(foreign.Id, new UpdateWebhookSubscriptionRequest(Url, null, ["ticket.opened"]), ct)).Errors
            .Should().ContainSingle(e => e.Code == WebhookErrorCodes.NotFound);
        (await sut.RotateSecretAsync(foreign.Id, ct)).Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.NotFound);
        (await sut.DisableAsync(foreign.Id, ct)).Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.NotFound);
        (await sut.EnableAsync(foreign.Id, ct)).Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.NotFound);
        var deleted = await sut.DeleteAsync(foreign.Id, ct);
        deleted.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.NotFound);
        deleted.Errors.Single().Type.Should().Be(ErrorType.NotFound);

        foreign.IsDeleted.Should().BeFalse();
        foreign.IsEnabled().Should().BeTrue();
        _store.Verify(s => s.FindAsync(foreign.Id, Tenant, It.IsAny<CancellationToken>()), Times.AtLeast(5));
        _store.Verify(s => s.FindAsync(It.IsAny<Guid>(), "tenant-b", It.IsAny<CancellationToken>()), Times.Never);
        _store.Verify(s => s.SaveAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Rotation (rule 35.15) --
    [Fact]
    public async Task Rotate_KeepsPreviousForGraceHours()
    {
        var subscription = SeedOwn("protected:old");

        var result = await CreateSut().RotateSecretAsync(subscription.Id, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        var rotated = result.Value!;
        var secret = rotated.Secret;
        secret.Should().StartWith("whsec_");
        _protector.Verify(p => p.Protect(secret), Times.Once);
        subscription.ProtectedSecret.Should().Be("protected:" + secret);
        subscription.PreviousProtectedSecret.Should().Be("protected:old");
        subscription.PreviousSecretExpiresOn.Should().Be(Now.AddHours(_settings.PreviousSecretGraceHours));
        subscription.GetSigningSecrets(Now.AddHours(_settings.PreviousSecretGraceHours)).Should().Equal("protected:" + secret);
        _store.Verify(s => s.SaveAsync(subscription, It.IsAny<CancellationToken>()), Times.Once);
        Serialize(rotated.Subscription).Should().NotContain(secret);
    }

    // -- No secret outside create and rotate (rule 35.14) --
    [Fact]
    public async Task List_NeverContainsSecret()
    {
        var first = WebhookSubscription.Create(Url, "One", ["ticket.opened"], Tenant, "protected:first-secret").Value!;
        var second = WebhookSubscription.Create(Url, "Two", ["*"], Tenant, "protected:second-secret").Value!;
        second.RotateSecret("protected:third-secret", Now.AddHours(1));
        _store.Setup(s => s.ListAsync(Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(new List<WebhookSubscription> { first, second });

        var result = await CreateSut().ListAsync(TestContext.Current.CancellationToken);

        result.Value.Should().HaveCount(2);
        var json = Serialize(result.Value!);
        json.Should().NotContain("first-secret").And.NotContain("second-secret").And.NotContain("third-secret").And.NotContain("protected:");
        _store.Verify(s => s.ListAsync(Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateEnableDisable_ResponsesNeverContainSecret()
    {
        var subscription = SeedOwn("protected:kept-secret");
        var sut = CreateSut();
        var ct = TestContext.Current.CancellationToken;

        var updated = await sut.UpdateAsync(subscription.Id, new UpdateWebhookSubscriptionRequest(Url, "Changed", ["ticket.closed"]), ct);
        var disabled = await sut.DisableAsync(subscription.Id, ct);
        var enabled = await sut.EnableAsync(subscription.Id, ct);

        Serialize(updated.Value!).Should().NotContain("kept-secret");
        Serialize(disabled.Value!).Should().NotContain("kept-secret");
        Serialize(enabled.Value!).Should().NotContain("kept-secret");
    }

    // -- Update, disable, enable, delete --
    [Fact]
    public async Task Update_ValidatesTheTargetAndCatalog_ThenSaves()
    {
        var subscription = SeedOwn("protected:s");

        var result = await CreateSut().UpdateAsync(
            subscription.Id, new UpdateWebhookSubscriptionRequest("https://other.example.com/h", "New", ["ticket.closed"]), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        var response = result.Value!;
        response.Url.Should().Be("https://other.example.com/h");
        response.Description.Should().Be("New");
        response.EventTypes.Should().Equal("ticket.closed");
        subscription.ProtectedSecret.Should().Be("protected:s");
        _targetValidator.Verify(v => v.ValidateAsync(new Uri("https://other.example.com/h"), It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.SaveAsync(subscription, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_UnknownEventType_Fails()
    {
        var subscription = SeedOwn("protected:s");

        var result = await CreateSut().UpdateAsync(
            subscription.Id, new UpdateWebhookSubscriptionRequest(Url, null, ["ticket.deleted"]), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.EventTypeUnknown);
        _store.Verify(s => s.SaveAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_TargetRefused_FailsTargetNotAllowed()
    {
        var subscription = SeedOwn("protected:s");
        _targetValidator.Setup(v => v.ValidateAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.Validation(WebhookErrorCodes.TargetNotAllowed, "Private address.")));

        var result = await CreateSut().UpdateAsync(
            subscription.Id, new UpdateWebhookSubscriptionRequest(Url, null, ["ticket.opened"]), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.TargetNotAllowed);
        _store.Verify(s => s.SaveAsync(It.IsAny<WebhookSubscription>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Disable_StampsTheClock_AndEnableClearsIt()
    {
        var subscription = SeedOwn("protected:s");
        var sut = CreateSut();

        var disabled = await sut.DisableAsync(subscription.Id, TestContext.Current.CancellationToken);

        disabled.Value!.IsEnabled.Should().BeFalse();
        subscription.DisabledOn.Should().Be(Now);

        var enabled = await sut.EnableAsync(subscription.Id, TestContext.Current.CancellationToken);

        enabled.Value!.IsEnabled.Should().BeTrue();
        subscription.DisabledOn.Should().BeNull();
        _store.Verify(s => s.SaveAsync(subscription, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Delete_SoftDeletes()
    {
        var subscription = SeedOwn("protected:s");

        var result = await CreateSut().DeleteAsync(subscription.Id, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        subscription.IsDeleted.Should().BeTrue("delete is the base soft delete");
        _store.Verify(s => s.SaveAsync(subscription, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_UnknownId_NotFound()
    {
        var result = await CreateSut().DeleteAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Errors.Should().ContainSingle(e => e.Code == WebhookErrorCodes.NotFound);
    }

    private static CreateWebhookSubscriptionRequest Request(IReadOnlyList<string> eventTypes) => new(Url, "Partner feed", eventTypes);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, WebJsonOptions);

    private WebhookSubscription SeedOwn(string protectedSecret)
    {
        var subscription = WebhookSubscription.Create(Url, null, ["ticket.opened"], Tenant, protectedSecret).Value!;
        _store.Setup(s => s.FindAsync(subscription.Id, Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(subscription);
        return subscription;
    }

    private WebhookSubscriptionService CreateSut() => new(
        _store.Object,
        _targetValidator.Object,
        _protector.Object,
        new WebhookEventCatalog(
        [
            new WebhookEventRegistration(typeof(WebhookServiceProbeOpened), "ticket.opened"),
            new WebhookEventRegistration(typeof(WebhookServiceProbeClosed), "ticket.closed"),
        ]),
        _tenant.Object,
        Options.Create(_settings),
        _clock);
}

// -- Test types --
public sealed record WebhookServiceProbeOpened(int TicketId) : BaseIntegrationEvent;

public sealed record WebhookServiceProbeClosed(int TicketId) : BaseIntegrationEvent;
