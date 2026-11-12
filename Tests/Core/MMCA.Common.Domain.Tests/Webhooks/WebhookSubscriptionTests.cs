using AwesomeAssertions;
using MMCA.Common.Domain.Webhooks;

namespace MMCA.Common.Domain.Tests.Webhooks;

/// <summary>
/// The <see cref="WebhookSubscription"/> row (item 35): what a valid target and event list look like,
/// how the event list is normalized, and how disable, rotation and the last-outcome columns behave.
/// The error codes are the literal "Webhooks.X" strings because the Domain shares them with the
/// Application's <c>WebhookErrorCodes</c>, which this project cannot reference.
/// </summary>
public sealed class WebhookSubscriptionTests
{
    private const string Url = "https://hooks.example.com/inbound";
    private const string Secret = "protected-secret-1";

    private static readonly DateTime Now = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string[] UnsortedEventTypes = ["b.x", "a_z", "a.z", "a-z"];

    // -- Create: event types (rule 35.11) --
    [Fact]
    public void Create_WithHttpsUrl_SortsAndDedupesEventTypes()
    {
        var result = WebhookSubscription.Create(
            Url, "Partner feed", ["ticket.opened", "order.paid", "ticket.opened", "invoice.sent"], "tenant-a", Secret);

        result.IsSuccess.Should().BeTrue();
        var subscription = result.Value!;
        subscription.EventTypes.Should().Be("invoice.sent,order.paid,ticket.opened", "stored ordinal-sorted, distinct, comma-joined");
        subscription.GetEventTypes().Should().Equal("invoice.sent", "order.paid", "ticket.opened");
        subscription.Url.Should().Be(Url);
        subscription.Description.Should().Be("Partner feed");
        subscription.TenantId.Should().Be("tenant-a");
        subscription.ProtectedSecret.Should().Be(Secret);
        subscription.IsEnabled().Should().BeTrue();
        subscription.DisabledOn.Should().BeNull();
        subscription.PreviousProtectedSecret.Should().BeNull();
    }

    [Fact]
    public void Create_SortsOrdinally_NotByCulture()
    {
        var subscription = WebhookSubscription.Create(Url, null, ["b.x", "a_z", "a.z", "a-z"], null, Secret).Value!;

        subscription.GetEventTypes().Should().Equal(UnsortedEventTypes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Create_AssignsADistinctNonEmptyId()
    {
        var first = WebhookSubscription.Create(Url, null, ["ticket.opened"], null, Secret).Value!;
        var second = WebhookSubscription.Create(Url, null, ["ticket.opened"], null, Secret).Value!;

        first.Id.Should().NotBe(Guid.Empty);
        first.Id.Should().NotBe(second.Id, "the delivery id is derived from the subscription id, so two rows must never share one");
    }

    [Fact]
    public void Create_WithNullTenant_KeepsTenantNull() => WebhookSubscription.Create(Url, null, ["ticket.opened"], null, Secret).Value!.TenantId.Should().BeNull();

    [Fact]
    public void Create_WithWildcard_IsAccepted()
    {
        var subscription = WebhookSubscription.Create(Url, null, [WebhookSubscription.AllEventTypes], null, Secret).Value!;

        subscription.EventTypes.Should().Be("*");
    }

    [Fact]
    public void Create_WithNoEventTypes_FailsEventTypesRequired() => WebhookSubscription.Create(Url, null, [], null, Secret).Errors
            .Should().ContainSingle(e => e.Code == "Webhooks.EventTypesRequired");

    [Fact]
    public void Create_With21EventTypes_FailsTooManyEventTypes()
    {
        var types = Enumerable.Range(1, WebhookSubscription.MaxEventTypes + 1).Select(i => $"event.n{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}").ToList();

        WebhookSubscription.Create(Url, null, types, null, Secret).Errors
            .Should().ContainSingle(e => e.Code == "Webhooks.TooManyEventTypes");
    }

    [Fact]
    public void Create_With20EventTypes_Succeeds()
    {
        var types = Enumerable.Range(1, WebhookSubscription.MaxEventTypes).Select(i => $"event.n{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}").ToList();

        WebhookSubscription.Create(Url, null, types, null, Secret).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithABlankEventType_Fails(string blank) =>
        // DESIGN-QUESTION: rule 35.11 says "each non-blank" without naming the code, so only the failure is asserted.
        WebhookSubscription.Create(Url, null, ["ticket.opened", blank], null, Secret).IsFailure.Should().BeTrue();

    // -- Create: URL (rule 35.10) --
    [Fact]
    public void Create_WithRelativeUrl_FailsUrlInvalid() => AssertUrlInvalid("/hooks/inbound");

    [Fact]
    public void Create_WithUserInfo_FailsUrlInvalid() => AssertUrlInvalid("https://user:pass@hooks.example.com/inbound");

    [Fact]
    public void Create_WithFragment_FailsUrlInvalid() => AssertUrlInvalid("https://hooks.example.com/inbound#section");

    [Fact]
    public void Create_WithFtpScheme_FailsUrlInvalid() => AssertUrlInvalid("ftp://hooks.example.com/inbound");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void Create_WithBlankOrMalformedUrl_FailsUrlInvalid(string url) => AssertUrlInvalid(url);

    [Fact]
    public void Create_WithUrlOver2048Chars_FailsUrlInvalid()
    {
        var url = "https://hooks.example.com/" + new string('a', WebhookSubscription.UrlMaxLength);

        AssertUrlInvalid(url);
    }

    [Fact]
    public void Create_WithHttpUrl_IsLeftToTheTargetValidator() =>
        // Scheme policy beyond https-or-http belongs to the validator (rule 35.10, 35.20).
        WebhookSubscription.Create("http://hooks.example.com/inbound", null, ["ticket.opened"], null, Secret)
            .IsSuccess.Should().BeTrue();

    // -- Create: description and secret --
    [Fact]
    public void Create_WithDescriptionOver200_FailsDescriptionTooLong()
    {
        var description = new string('d', WebhookSubscription.DescriptionMaxLength + 1);

        WebhookSubscription.Create(Url, description, ["ticket.opened"], null, Secret).Errors
            .Should().ContainSingle(e => e.Code == "Webhooks.DescriptionTooLong");
    }

    [Fact]
    public void Create_WithDescriptionOf200_Succeeds()
    {
        var description = new string('d', WebhookSubscription.DescriptionMaxLength);

        WebhookSubscription.Create(Url, description, ["ticket.opened"], null, Secret).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankSecret_FailsSecretRequired(string secret) => WebhookSubscription.Create(Url, null, ["ticket.opened"], null, secret).Errors
            .Should().ContainSingle(e => e.Code == "Webhooks.SecretRequired");

    // -- Update --
    [Fact]
    public void Update_ReplacesUrlDescriptionAndEventTypes_AndKeepsTheSecret()
    {
        var subscription = NewSubscription();

        var result = subscription.Update("https://other.example.com/hook", "New", ["order.paid", "order.paid", "a.b"]);

        result.IsSuccess.Should().BeTrue();
        subscription.Url.Should().Be("https://other.example.com/hook");
        subscription.Description.Should().Be("New");
        subscription.EventTypes.Should().Be("a.b,order.paid");
        subscription.ProtectedSecret.Should().Be(Secret);
    }

    [Fact]
    public void Update_WithFtpScheme_FailsUrlInvalid() => NewSubscription().Update("ftp://hooks.example.com/inbound", null, ["ticket.opened"]).Errors
            .Should().ContainSingle(e => e.Code == "Webhooks.UrlInvalid");

    [Fact]
    public void Update_WithNoEventTypes_FailsEventTypesRequired() => NewSubscription().Update(Url, null, []).Errors
            .Should().ContainSingle(e => e.Code == "Webhooks.EventTypesRequired");

    // -- Subscribes --
    [Fact]
    public void Subscribes_ListedTypeAndWildcard_TrueOtherwiseFalse()
    {
        var listed = WebhookSubscription.Create(Url, null, ["ticket.opened", "order.paid"], null, Secret).Value!;
        var wildcard = WebhookSubscription.Create(Url, null, ["*"], null, Secret).Value!;

        listed.Subscribes("ticket.opened").Should().BeTrue();
        listed.Subscribes("order.paid").Should().BeTrue();
        listed.Subscribes("ticket.closed").Should().BeFalse();
        listed.Subscribes("ticket").Should().BeFalse("a prefix of a listed type is a different type");
        wildcard.Subscribes("ticket.opened").Should().BeTrue();
        wildcard.Subscribes("anything.else").Should().BeTrue();
    }

    // -- Disable and enable (rule 35.17) --
    [Fact]
    public void Disable_Twice_KeepsFirstInstant()
    {
        var subscription = NewSubscription();

        subscription.Disable(Now);
        subscription.Disable(Now.AddHours(1));

        subscription.DisabledOn.Should().Be(Now);
        subscription.IsEnabled().Should().BeFalse();
    }

    [Fact]
    public void Enable_ClearsDisabledOn()
    {
        var subscription = NewSubscription();
        subscription.Disable(Now);

        subscription.Enable();

        subscription.DisabledOn.Should().BeNull();
        subscription.IsEnabled().Should().BeTrue();
    }

    // -- Secrets (rule 35.15) --
    [Fact]
    public void RotateSecret_MovesCurrentToPrevious()
    {
        var subscription = NewSubscription();
        var expires = Now.AddHours(24);

        subscription.RotateSecret("protected-secret-2", expires);

        subscription.ProtectedSecret.Should().Be("protected-secret-2");
        subscription.PreviousProtectedSecret.Should().Be(Secret);
        subscription.PreviousSecretExpiresOn.Should().Be(expires);
    }

    [Fact]
    public void GetSigningSecrets_WithoutRotation_ReturnsOnlyTheCurrent() => NewSubscription().GetSigningSecrets(Now).Should().Equal(Secret);

    [Fact]
    public void GetSigningSecrets_DuringGrace_ReturnsCurrentFirstThenPrevious()
    {
        var subscription = NewSubscription();
        subscription.RotateSecret("protected-secret-2", Now.AddHours(24));

        subscription.GetSigningSecrets(Now.AddHours(23)).Should().Equal("protected-secret-2", Secret);
    }

    [Fact]
    public void GetSigningSecrets_DropsPreviousAtExpiry()
    {
        var subscription = NewSubscription();
        var expires = Now.AddHours(24);
        subscription.RotateSecret("protected-secret-2", expires);

        subscription.GetSigningSecrets(expires.AddTicks(-1)).Should().Equal("protected-secret-2", Secret);
        subscription.GetSigningSecrets(expires).Should().ContainSingle()
            .Which.Should().Be("protected-secret-2", "previous is used only while now < PreviousSecretExpiresOn");
        subscription.GetSigningSecrets(expires.AddHours(1)).Should().Equal("protected-secret-2");
    }

    // -- Last outcome (rule 35.34) --
    [Fact]
    public void RecordDelivered_StampsLastDeliveredOn()
    {
        var subscription = NewSubscription();

        subscription.RecordDelivered(Now);

        subscription.LastDeliveredOn.Should().Be(Now);
    }

    [Fact]
    public void RecordFailed_StoresReasonAndInstant()
    {
        var subscription = NewSubscription();

        subscription.RecordFailed(Now, "HTTP 500");

        subscription.LastFailedOn.Should().Be(Now);
        subscription.LastFailure.Should().Be("HTTP 500");
    }

    [Fact]
    public void RecordFailed_TruncatesTo500()
    {
        var subscription = NewSubscription();
        var reason = "HttpRequestException: " + new string('x', 700);

        subscription.RecordFailed(Now, reason);

        subscription.LastFailure.Should().HaveLength(WebhookSubscription.LastFailureMaxLength);
        subscription.LastFailure.Should().Be(reason[..WebhookSubscription.LastFailureMaxLength]);
    }

    private static void AssertUrlInvalid(string url) =>
        WebhookSubscription.Create(url, null, ["ticket.opened"], null, Secret).Errors
            .Should().Contain(e => e.Code == "Webhooks.UrlInvalid");

    private static WebhookSubscription NewSubscription() =>
        WebhookSubscription.Create(Url, "Partner feed", ["ticket.opened"], "tenant-a", Secret).Value!;
}
