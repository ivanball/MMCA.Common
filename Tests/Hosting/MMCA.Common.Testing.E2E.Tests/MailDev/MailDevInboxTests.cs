using AwesomeAssertions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.MailDev;
using MMCA.Common.Testing.E2E.Tests.Infrastructure;
using Moq;

namespace MMCA.Common.Testing.E2E.Tests.MailDev;

/// <summary>
/// Covers <see cref="MailDevInbox"/> without a running MailDev: the default address is the port
/// <c>AddMailDev</c> publishes, the message list parses, the 2.x path is the fallback, and a wait
/// returns the newest match once the count is reached.
/// </summary>
[Collection(E2EEnvironmentCollection.Name)]
public sealed class MailDevInboxTests
{
    private const string TwoMessages =
        "[" +
        "{ \"id\": \"a1\", \"subject\": \"Your sign-in link\", \"html\": \"<a href=\\\"https://app.test/signin?token=x&amp;r=1\\\">Sign in</a>\", \"to\": [ { \"address\": \"User@Test.dev\" } ] }," +
        "{ \"id\": \"b2\", \"subject\": \"Order Received - Order #42\", \"html\": \"<p>Thanks</p>\", \"to\": [ { \"address\": \"user@test.dev\" }, { \"name\": \"no address\" } ] }" +
        "]";

    /// <summary>The default address is the IPv4 loopback on the hosting port.</summary>
    [Fact]
    public void BaseUrl_DefaultsToTheIpv4LoopbackOnTheHostingPort()
    {
        using var scope = new EnvironmentScope(MailDevInbox.BaseUrlVariable, null);

        // 1080 is MailDevDefaults.HttpPort, the one linked source file Aspire.Hosting publishes MailDev on.
        MailDevInbox.BaseUrl.Should().Be("http://127.0.0.1:1080");
    }

    /// <summary>The override is honored, without a trailing slash.</summary>
    [Fact]
    public void BaseUrl_HonorsTheOverride_WithoutATrailingSlash()
    {
        using var scope = new EnvironmentScope(MailDevInbox.BaseUrlVariable, "http://maildev:1080/");

        MailDevInbox.BaseUrl.Should().Be("http://maildev:1080");
    }

    /// <summary>Parsing reads ids, subjects, recipients and the decoded HTML.</summary>
    [Fact]
    public void ParseMessages_ReadsIdsSubjectsRecipientsAndDecodedHtml()
    {
        var messages = MailDevInbox.ParseMessages(TwoMessages);

        messages.Should().HaveCount(2);
        messages[0].Id.Should().Be("a1");
        messages[0].Html.Should().Contain("token=x&r=1");
        messages[1].To.Should().Equal("user@test.dev");
    }

    /// <summary>The first matching href is returned, and a missing one throws.</summary>
    [Fact]
    public void LinkContaining_ReturnsTheFirstMatchingHref_OrThrows()
    {
        var message = MailDevInbox.ParseMessages(TwoMessages)[0];

        MailDevInbox.LinkContaining(message, "/signin").Should().Be(new Uri("https://app.test/signin?token=x&r=1"));
        var missing = () => MailDevInbox.LinkContaining(message, "/reset");
        missing.Should().Throw<InvalidOperationException>().WithMessage("*no link containing '/reset'*");
    }

    /// <summary>The exact-subject wait matches the recipient case-insensitively.</summary>
    [Fact]
    public async Task WaitForMessageAsync_MatchesExactSubjectAndRecipientCaseInsensitively()
    {
        using var scope = new EnvironmentScope(MailDevInbox.BaseUrlVariable, "http://maildev.test");
        var api = Api(("http://maildev.test/api/email", 200, TwoMessages));

        var message = await MailDevInbox.WaitForMessageAsync(api.Object, "user@test.dev", "Your sign-in link", timeoutSeconds: 1);

        message.Id.Should().Be("a1");
    }

    /// <summary>The prefix wait falls back to the MailDev 2.x path on a 404.</summary>
    [Fact]
    public async Task WaitForMessageWithSubjectPrefixAsync_FallsBackToTheV2Path()
    {
        using var scope = new EnvironmentScope(MailDevInbox.BaseUrlVariable, "http://maildev.test");
        var api = Api(("http://maildev.test/api/email", 404, string.Empty), ("http://maildev.test/email", 200, TwoMessages));

        var message = await MailDevInbox.WaitForMessageWithSubjectPrefixAsync(api.Object, "USER@test.dev", "Order Received - Order #", timeoutSeconds: 1);

        message.Id.Should().Be("b2");
    }

    /// <summary>A wait that times out names the shortfall.</summary>
    [Fact]
    public async Task WaitForMessageAsync_TimesOut_NamingTheShortfall()
    {
        using var scope = new EnvironmentScope(MailDevInbox.BaseUrlVariable, "http://maildev.test");
        var api = Api(("http://maildev.test/api/email", 200, TwoMessages));

        var act = () => MailDevInbox.WaitForMessageAsync(api.Object, "user@test.dev", "Your sign-in link", count: 2, timeoutSeconds: 0);

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("Only 1 of 2 emails to user@test.dev*");
    }

    /// <summary>The unreachable reason names the HTTP status, or is null when MailDev answers.</summary>
    [Fact]
    public async Task UnreachableReasonAsync_NamesTheStatus_OrReturnsNull()
    {
        using var scope = new EnvironmentScope(MailDevInbox.BaseUrlVariable, "http://maildev.test");

        (await MailDevInbox.UnreachableReasonAsync(Api(("http://maildev.test/api/email", 200, "[]")).Object)).Should().BeNull();
        (await MailDevInbox.UnreachableReasonAsync(Api(("http://maildev.test/api/email", 503, string.Empty)).Object)).Should().Be("HTTP 503");
    }

    private static Mock<IAPIRequestContext> Api(params (string Url, int Status, string Body)[] routes)
    {
        var api = new Mock<IAPIRequestContext>(MockBehavior.Strict);
        foreach (var (url, status, body) in routes)
        {
            var response = new Mock<IAPIResponse>();
            response.SetupGet(r => r.Status).Returns(status);
            response.SetupGet(r => r.Ok).Returns(status is >= 200 and < 300);
            response.Setup(r => r.TextAsync()).ReturnsAsync(body);
            api.Setup(a => a.GetAsync(url, It.IsAny<APIRequestContextOptions?>())).ReturnsAsync(response.Object);
        }

        return api;
    }
}
