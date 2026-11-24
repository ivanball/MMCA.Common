#pragma warning disable SA1507, SA1512, SA1203, SA1137, MA0159, MA0076, IDE0062, S9415, RCS1257, IDE0055 // temporary differential harness
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;
using AwesomeAssertions;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Azure.NotificationHubs.Messaging;
using MMCA.Common.Azure.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;


namespace MMCA.Common.Azure.Tests.Notifications;

/// <summary>
/// TEMPORARY differential oracle: drives the Microsoft.Azure.NotificationHubs 4.2.0 SDK and the own
/// REST client with the same inputs against the same recording handler and requires identical
/// traffic. Deleted once the SDK is removed; its captured values live on in the request-shape tests.
/// </summary>
public sealed class NotificationHubSdkDifferentialTests(ITestOutputHelper output)
{
    private const string HubName = "mmca-hub";
    private const string ConnectionString =
        "Endpoint=sb://mmca-ns.servicebus.windows.net/;SharedAccessKeyName=DefaultFullSharedAccessSignature;SharedAccessKey=c2VjcmV0LWtleS1mb3ItdGVzdHMtb25seQ==";

    private static readonly string TrickyId = "id with space?#%41/" + (char)0x2028 + "é\U0001F600";

    // ---------------------------------------------------------------- harness

    private sealed record Recorded(string Method, string AbsoluteUri, string UriString, List<KeyValuePair<string, string>> Headers, List<KeyValuePair<string, string>> ContentHeaders, byte[]? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[]? body = null;
            var contentHeaders = new List<KeyValuePair<string, string>>();
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                _ = request.Content.Headers.ContentLength;
                contentHeaders.AddRange(request.Content.Headers.Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value))));
            }

            Requests.Add(new Recorded(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.RequestUri.ToString(),
                [.. request.Headers.Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)))],
                contentHeaders,
                body));

            var response = respond(request, Requests.Count);
            response.RequestMessage = request;
            return response;
        }
    }

    private static NotificationHubClient Sdk(RecordingHandler handler) =>
        new(ConnectionString, HubName, new NotificationHubSettings
        {
            MessageHandler = handler,
            RetryOptions = new NotificationHubRetryOptions { MaxRetries = 3, Delay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(5) },
        });

    private static readonly NotificationHubConnection Connection = NotificationHubConnection.Parse(ConnectionString, HubName);

    private static NotificationHubRestClient Own(RecordingHandler handler) =>
        new(new HttpClient(handler), Connection, TimeProvider.System, new MMCA.Common.Azure.Notifications.NotificationHubRetryPolicy(3, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5)));

    private static Func<HttpRequestMessage, int, HttpResponseMessage> Always(Func<HttpResponseMessage> make) => (_, _) => make();

    private static readonly System.Threading.Lock LogGate = new();

    private int _compared;

    private void Log(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("NH_DIFF_LOG") is { Length: > 0 } path)
        {
            lock (LogGate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
    }

    /// <summary>Compares one request pair: everything byte for byte except the SAS sig/se and the TrackingId value.</summary>
    private void AssertSameRequest(Recorded sdk, Recorded own, string label)
    {
        own.Method.Should().Be(sdk.Method, label);
        own.AbsoluteUri.Should().Be(sdk.AbsoluteUri, label);
        own.UriString.Should().Be(sdk.UriString, label);
        own.Headers.Select(h => h.Key).Should().Equal(sdk.Headers.Select(h => h.Key), $"{label}: header names and order");
        foreach (var (name, sdkValue) in sdk.Headers)
        {
            var ownValue = own.Headers.Single(h => h.Key == name).Value;
            if (name == "TrackingId")
            {
                Guid.TryParse(ownValue, out _).Should().BeTrue(label);
                Guid.TryParse(sdkValue, out _).Should().BeTrue(label);
            }
            else if (name == "Authorization")
            {
                AssertSameSignature(sdkValue, ownValue, own.UriString, label);
            }
            else
            {
                ownValue.Should().Be(sdkValue, $"{label}: header {name}");
            }
        }

        own.ContentHeaders.Should().Equal(sdk.ContentHeaders, $"{label}: content headers");
        if (sdk.Body is null)
        {
            own.Body.Should().BeNull(label);
        }
        else
        {
            own.Body.Should().Equal(sdk.Body, $"{label}: body bytes");
        }

        _compared++;
        Log($"RAW {label} URI={sdk.AbsoluteUri} AUTH={sdk.Headers.Single(h => h.Key == "Authorization").Value} BODY64={(sdk.Body is null ? "-" : Convert.ToBase64String(sdk.Body))}");
        Log($"{label}: {sdk.Method} {sdk.AbsoluteUri} | {string.Join(" | ", sdk.Headers.Where(h => h.Key is not ("Authorization" or "TrackingId")).Select(h => h.Key + "=" + h.Value))} | {string.Join(" | ", sdk.ContentHeaders.Select(h => h.Key + "=" + h.Value))} | {(sdk.Body is null ? "<no body>" : Encoding.UTF8.GetString(sdk.Body))}");
    }

    private static Dictionary<string, string> SasParts(string header)
    {
        header.Should().StartWith("SharedAccessSignature ");
        return header["SharedAccessSignature ".Length..].Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    }

    /// <summary>
    /// sr and skn equal; then the own token is regenerated on a clock fixed so its expiry equals the
    /// SDK's, and the WHOLE header must equal the SDK's (so sig and se are checked too).
    /// </summary>
    private static void AssertSameSignature(string sdkHeader, string ownHeader, string ownUri, string label)
    {
        var sdkParts = SasParts(sdkHeader);
        var ownParts = SasParts(ownHeader);
        ownParts.Keys.Should().Equal(sdkParts.Keys, label);
        ownParts["sr"].Should().Be(sdkParts["sr"], $"{label}: sr");
        ownParts["skn"].Should().Be(sdkParts["skn"], $"{label}: skn");

        var expiry = long.Parse(HttpUtility.UrlDecode(sdkParts["se"]), System.Globalization.CultureInfo.InvariantCulture);
        var fixedNow = DateTimeOffset.FromUnixTimeSeconds(expiry) - NotificationHubConnection.TokenTimeToLive;
        Connection.CreateSharedAccessSignature(new Uri(ownUri), fixedNow).Should().Be(sdkHeader, $"{label}: full SAS on a fixed clock");
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string? body = null, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(code);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        configure?.Invoke(response);
        return response;
    }

    // ---------------------------------------------------------------- installations

    public static TheoryData<string, string, string, string[]> Installations => new()
    {
        { "fcm", "3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d", "dGVzdA:APA91bH_fcm-registration-token-0123456789", ["user:42"] },
        { "apns", "94018932-9656-48dd-aea1-0c2253b4e093", "62BCD3484834B7E2DC56733AC5509259230CC1BA82BA1AE7D06E91908A05A8FA", ["user:7"] },
        { "fcm", TrickyId, "chan\"nel\\with\ttab\n\r\f\b\u0001\u001f\u007f" + (char)0x85 + (char)0x2028 + (char)0x2029 + "'<>&é\U0001F600", ["user:1", "tag \"q\" \\ " + (char)0x2029] },
    };

    [Theory]
    [MemberData(nameof(Installations))]
    public async Task CreateOrUpdateInstallation_SameRequest(string platform, string id, string channel, string[] tags)
    {
        var sdkHandler = new RecordingHandler(Always(() => Status(HttpStatusCode.OK)));
        var ownHandler = new RecordingHandler(Always(() => Status(HttpStatusCode.OK)));

        await Sdk(sdkHandler).CreateOrUpdateInstallationAsync(new Installation
        {
            InstallationId = id,
            Platform = platform == "fcm" ? NotificationPlatform.FcmV1 : NotificationPlatform.Apns,
            PushChannel = channel,
            Tags = [.. tags],
        }, TestContext.Current.CancellationToken);
        await Own(ownHandler).CreateOrUpdateInstallationAsync(
            new NotificationHubInstallation(id, platform == "fcm" ? NotificationHubPlatform.FcmV1 : NotificationHubPlatform.Apns, channel, tags),
            TestContext.Current.CancellationToken);

        AssertSameRequest(sdkHandler.Requests.Single(), ownHandler.Requests.Single(), $"PUT {platform}");
    }

    public static TheoryData<string, string> InstallationReads => new()
    {
        { "3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d", "{\"installationId\":\"94018932\",\"pushChannel\":\"62BCD34848\",\"pushChannelExpired\":false,\"platform\":\"apns\",\"expirationTime\":\"9999-12-31T23:59:59.9999999Z\",\"tags\":[\"user:42\",\"Other\"]}" },
        { TrickyId, "{\"installationId\":\"x\",\"pushChannel\":\"fcm:tok\",\"platform\":\"fcmV1\",\"tags\":[\"user:1\"],\"templates\":{\"t\":{\"body\":\"{}\"}}}" },
        { "abc", "{\"installationId\":\"x\",\"PUSHCHANNEL\":\"upper\",\"platform\":\"gcm\"}" },
        { "abc", "{\"installationId\":\"x\",\"pushChannel\":null,\"platform\":\"apns\",\"tags\":null}" },
    };

    [Theory]
    [MemberData(nameof(InstallationReads))]
    public async Task GetInstallation_SameRequest_SameParsedResult(string id, string responseJson)
    {
        var sdkHandler = new RecordingHandler(Always(() => Status(HttpStatusCode.OK, responseJson)));
        var ownHandler = new RecordingHandler(Always(() => Status(HttpStatusCode.OK, responseJson)));

        var sdkResult = await Sdk(sdkHandler).GetInstallationAsync(id, TestContext.Current.CancellationToken);
        var ownResult = await Own(ownHandler).GetInstallationAsync(id, TestContext.Current.CancellationToken);

        AssertSameRequest(sdkHandler.Requests.Single(), ownHandler.Requests.Single(), "GET installation");
        ownResult.Should().NotBeNull();
        ownResult!.PushChannel.Should().Be(sdkResult.PushChannel);
        if (sdkResult.Tags is null)
        {
            ownResult.Tags.Should().BeNull();
        }
        else
        {
            ownResult.Tags.Should().Equal(sdkResult.Tags);
        }
    }

    [Theory]
    [InlineData("3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d")]
    [InlineData("id with space?#%41/")]
    public async Task DeleteInstallation_SameRequest(string id)
    {
        var sdkHandler = new RecordingHandler(Always(() => Status(HttpStatusCode.NoContent)));
        var ownHandler = new RecordingHandler(Always(() => Status(HttpStatusCode.NoContent)));

        await Sdk(sdkHandler).DeleteInstallationAsync(id, TestContext.Current.CancellationToken);
        await Own(ownHandler).DeleteInstallationAsync(id, TestContext.Current.CancellationToken);

        AssertSameRequest(sdkHandler.Requests.Single(), ownHandler.Requests.Single(), "DELETE installation");
    }

    // ---------------------------------------------------------------- registrations by tag

    private const string Ns = "http://schemas.microsoft.com/netservices/2010/10/servicebus/connect";

    private static string Entry(string element, string tagsElement, string extra) =>
        "<entry a:etag=\"W/&quot;1&quot;\" xmlns:a=\"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata\"><id>https://mmca-ns.servicebus.windows.net/mmca-hub/registrations/1?api-version=2020-06</id><title type=\"text\">1</title><published>2026-11-24T00:00:00Z</published><updated>2026-11-24T00:00:00Z</updated><link rel=\"self\" href=\"https://mmca-ns.servicebus.windows.net/mmca-hub/registrations/1?api-version=2020-06\"/>"
        + $"<content type=\"application/xml\"><{element} xmlns=\"{Ns}\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><ETag>1</ETag><ExpirationTime>9999-12-31T23:59:59.9999999Z</ExpirationTime><RegistrationId>1</RegistrationId>{tagsElement}{extra}</{element}></content></entry>";

    private static string Feed(params string[] entries) =>
        "<feed xmlns=\"http://www.w3.org/2005/Atom\"><title type=\"text\">Registrations</title><id>https://mmca-ns.servicebus.windows.net/mmca-hub/tags/user:42/registrations?api-version=2020-06</id><updated>2026-11-24T00:00:00Z</updated><link rel=\"self\" href=\"https://mmca-ns.servicebus.windows.net/mmca-hub/tags/user:42/registrations?api-version=2020-06\"/>"
        + string.Concat(entries) + "</feed>";

    private const string FcmExtra = "<FcmV1RegistrationId>fcm-token</FcmV1RegistrationId>";
    private const string ApnsExtra = "<DeviceToken>62BCD3484834B7E2DC56733AC5509259230CC1BA82BA1AE7D06E91908A05A8FA</DeviceToken>";

    // The first two feeds are verbatim hub responses recorded in the SDK repo's MockData.
    private const string RecordedAppleFeed = "<feed xmlns=\"http://www.w3.org/2005/Atom\"><title type=\"text\">Registrations</title><id>https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/tags/tag1/registrations?api-version=2017-04&amp;$top=100</id><updated>2018-11-07T00:18:23Z</updated><link rel=\"self\" href=\"https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/tags/tag1/registrations?api-version=2017-04&amp;$top=100\"/><entry a:etag=\"W/&quot;1&quot;\" xmlns:a=\"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata\"><id>https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/tags/tag1/registrations/1493776553344450975-2363723502764053081-2?api-version=2017-04&amp;$top=100</id><title type=\"text\">1493776553344450975-2363723502764053081-2</title><published>2018-11-07T00:18:23Z</published><updated>2018-11-07T00:18:23Z</updated><link rel=\"self\" href=\"https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/tags/tag1/registrations/1493776553344450975-2363723502764053081-2?api-version=2017-04&amp;$top=100\"/><content type=\"application/xml\"><AppleRegistrationDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><ETag>1</ETag><ExpirationTime>9999-12-31T23:59:59.999Z</ExpirationTime><RegistrationId>1493776553344450975-2363723502764053081-2</RegistrationId><Tags>tag1</Tags><DeviceToken>62BCD3484834B7E2DC56733AC5509259230CC1BA82BA1AE7D06E91908A05A8FA</DeviceToken></AppleRegistrationDescription></content></entry><entry a:etag=\"W/&quot;1&quot;\" xmlns:a=\"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata\"><id>https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/tags/tag1/registrations/3013254082390575573-1236119253991496323-3?api-version=2017-04&amp;$top=100</id><title type=\"text\">3013254082390575573-1236119253991496323-3</title><published>2018-11-07T00:18:23Z</published><updated>2018-11-07T00:18:23Z</updated><link rel=\"self\" href=\"https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/tags/tag1/registrations/3013254082390575573-1236119253991496323-3?api-version=2017-04&amp;$top=100\"/><content type=\"application/xml\"><AppleRegistrationDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><ETag>1</ETag><ExpirationTime>9999-12-31T23:59:59.999Z</ExpirationTime><RegistrationId>3013254082390575573-1236119253991496323-3</RegistrationId><Tags>tag1</Tags><DeviceToken>62BCD3484834B7E2DC56733AC5509259230CC1BA82BA1AE7D06E91908A05A8FA</DeviceToken></AppleRegistrationDescription></content></entry></feed>";

    private const string RecordedWindowsFeed = "<feed xmlns=\"http://www.w3.org/2005/Atom\"><title type=\"text\">Registrations</title><id>https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/registrations?api-version=2017-04&amp;$top=100</id><updated>2018-11-07T00:18:22Z</updated><link rel=\"self\" href=\"https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/registrations?api-version=2017-04&amp;$top=100\"/><entry a:etag=\"W/&quot;1&quot;\" xmlns:a=\"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata\"><id>https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/registrations/4170287247964535769-8745470796860785881-3?api-version=2017-04&amp;$top=100</id><title type=\"text\">4170287247964535769-8745470796860785881-3</title><published>2018-11-07T00:18:22Z</published><updated>2018-11-07T00:18:22Z</updated><link rel=\"self\" href=\"https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/registrations/4170287247964535769-8745470796860785881-3?api-version=2017-04&amp;$top=100\"/><content type=\"application/xml\"><WindowsRegistrationDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><ETag>1</ETag><ExpirationTime>9999-12-31T23:59:59.9999999Z</ExpirationTime><RegistrationId>4170287247964535769-8745470796860785881-3</RegistrationId><Tags>$InstallationId:{ef4ffd64-8f20-4d6e-873c-7308b28d6a04}</Tags><ChannelUri>https://some.url/</ChannelUri></WindowsRegistrationDescription></content></entry><entry a:etag=\"W/&quot;1&quot;\" xmlns:a=\"http://schemas.microsoft.com/ado/2007/08/dataservices/metadata\"><id>https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/registrations/5763989116028704920-1411960204327425395-3?api-version=2017-04&amp;$top=100</id><title type=\"text\">5763989116028704920-1411960204327425395-3</title><published>2018-11-07T00:18:22Z</published><updated>2018-11-07T00:18:22Z</updated><link rel=\"self\" href=\"https://sdk-sample-namespace.servicebus.windows.net/sdk-sample-nh/registrations/5763989116028704920-1411960204327425395-3?api-version=2017-04&amp;$top=100\"/><content type=\"application/xml\"><WindowsTemplateRegistrationDescription xmlns=\"http://schemas.microsoft.com/netservices/2010/10/servicebus/connect\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><ETag>1</ETag><ExpirationTime>9999-12-31T23:59:59.9999999Z</ExpirationTime><RegistrationId>5763989116028704920-1411960204327425395-3</RegistrationId><Tags>$InstallationId:{ef4ffd64-8f20-4d6e-873c-7308b28d6a04},Template Name</Tags><ChannelUri>https://some.url/</ChannelUri><BodyTemplate><![CDATA[{\"aps\":{\"alert\":\"alert!\"}}]]></BodyTemplate><WnsHeaders><WnsHeader><Header>X-WNS-Type</Header><Value>wns/toast</Value></WnsHeader></WnsHeaders><TemplateName>Template Name</TemplateName></WindowsTemplateRegistrationDescription></content></entry></feed>";

    public static TheoryData<string, string?, string, string?> Feeds => new()
    {
        { "user:42", null, RecordedAppleFeed, null },
        { "user:42", string.Empty, RecordedWindowsFeed, "next+page/token==" },
        { "user:42", "next+page/token==", Feed(
            Entry("FcmV1RegistrationDescription", "<Tags>$InstallationId:{3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d},user:42</Tags>", FcmExtra),
            Entry("AppleRegistrationDescription", "<Tags>user:42,$installationid:{SECOND},USER:42</Tags>", ApnsExtra),
            Entry("FcmV1RegistrationDescription", "<Tags>user:42,$InstallationId:{a},$InstallationId:{b}</Tags>", FcmExtra),
            Entry("FcmV1RegistrationDescription", "<Tags>user:42," + new string('x', 121) + "</Tags>", FcmExtra),
            Entry("FcmV1RegistrationDescription", "<Tags>user:42," + new string('x', 120) + ",$UserId:{u@x.com}</Tags>", FcmExtra),
            Entry("FcmV1RegistrationDescription", string.Empty, FcmExtra),
            Entry("FcmV1RegistrationDescription", "<Tags>user:42,,x</Tags>", FcmExtra),
            Entry("AppleTemplateRegistrationDescription", "<Tags>$InstallationId:{c},user:42,tmpl</Tags>", ApnsExtra + "<BodyTemplate><![CDATA[{\"aps\":{}}]]></BodyTemplate><TemplateName>tmpl</TemplateName>"),
            Entry("FcmV1TemplateRegistrationDescription", "<Tags>$InstallationId:{d=e},user:42</Tags>", FcmExtra + "<BodyTemplate><![CDATA[{}]]></BodyTemplate><TemplateName>t</TemplateName>")), "page-3" },
        { "user:42", "page-3", Feed(), null },
    };

    [Theory]
    [MemberData(nameof(Feeds))]
    public async Task GetRegistrationsByTag_SameRequest_SameParsedTags(string tag, string? continuationToken, string feed, string? nextToken)
    {
        HttpResponseMessage Respond()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(feed, Encoding.UTF8, "application/atom+xml"),
            };
            if (nextToken is not null)
            {
                response.Headers.Add("x-ms-continuationtoken", nextToken);
            }

            return response;
        }

        var sdkHandler = new RecordingHandler(Always(Respond));
        var ownHandler = new RecordingHandler(Always(Respond));

        var sdkPage = await Sdk(sdkHandler).GetRegistrationsByTagAsync(tag, continuationToken, 100, TestContext.Current.CancellationToken);
        var ownPage = await Own(ownHandler).GetRegistrationsByTagAsync(tag, continuationToken, 100, TestContext.Current.CancellationToken);

        AssertSameRequest(sdkHandler.Requests.Single(), ownHandler.Requests.Single(), "GET registrations by tag");
        ownPage.ContinuationToken.Should().Be(sdkPage.ContinuationToken);
        var sdkTags = sdkPage.Select(r => (r.Tags ?? new HashSet<string>()).OrderBy(t => t, StringComparer.Ordinal).ToList()).ToList();
        var ownTags = ownPage.RegistrationTags.Select(t => t.OrderBy(x => x, StringComparer.Ordinal).ToList()).ToList();
        ownTags.Should().HaveCount(sdkTags.Count);
        for (var i = 0; i < sdkTags.Count; i++)
        {
            ownTags[i].Should().Equal(sdkTags[i], $"registration {i}");
            Log($"registration {i}: [{string.Join(", ", sdkTags[i])}]");
        }
    }

    // ---------------------------------------------------------------- sends

    public static TheoryData<string, string> Sends => new()
    {
        { "fcm", string.Join(" || ", Enumerable.Range(1, 20).Select(i => $"user:{i}")) },
        { "apns", string.Join(" || ", Enumerable.Range(1, 20).Select(i => $"user:{i}")) },
        { "fcm", "user:7" },
        { "apns", string.Empty },
    };

    [Theory]
    [MemberData(nameof(Sends))]
    public async Task SendNotification_SameRequest(string platform, string tagExpression)
    {
        var metadata = new Dictionary<string, string> { ["route"] = "/events/1?x=\"y\"", ["emoji"] = "café \U0001F600", ["ctl"] = "a\tb" };
        var payload = platform == "fcm"
            ? NativePushPayloads.BuildFcmV1Payload("Title \"q\"", "Body <b>&", metadata)
            : NativePushPayloads.BuildApnsPayload("Title \"q\"", "Body <b>&", metadata);
        HttpResponseMessage Respond() => Status(HttpStatusCode.Created, configure: r => r.Headers.Location = new Uri("https://mmca-ns.servicebus.windows.net/mmca-hub/messages/123?api-version=2020-06"));
        var sdkHandler = new RecordingHandler(Always(Respond));
        var ownHandler = new RecordingHandler(Always(Respond));

        Notification sdkNotification = platform == "fcm" ? new FcmV1Notification(payload) : new AppleNotification(payload);
        await Sdk(sdkHandler).SendNotificationAsync(sdkNotification, tagExpression, TestContext.Current.CancellationToken);
        await Own(ownHandler).SendNotificationAsync(
            platform == "fcm" ? NotificationHubNotification.FcmV1(payload) : NotificationHubNotification.Apple(payload),
            tagExpression,
            TestContext.Current.CancellationToken);

        AssertSameRequest(sdkHandler.Requests.Single(), ownHandler.Requests.Single(), $"POST {platform}");
    }

    // ---------------------------------------------------------------- failures and retries

    public static TheoryData<string> Operations => new() { "put", "get", "delete", "list", "send" };

    private static readonly (string Name, Func<HttpResponseMessage> Make)[] Failures =
    [
        ("404", () => Status(HttpStatusCode.NotFound, "<Error><Code>404</Code><Detail>Installation not found.TrackingId:x</Detail></Error>")),
        ("409", () => Status(HttpStatusCode.Conflict)),
        ("410", () => Status(HttpStatusCode.Gone)),
        ("429", () => Status((HttpStatusCode)429)),
        ("429 retry-after 0", () => Status((HttpStatusCode)429, configure: r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero))),
        ("500", () => Status(HttpStatusCode.InternalServerError)),
        ("503", () => Status(HttpStatusCode.ServiceUnavailable)),
        ("504", () => Status(HttpStatusCode.GatewayTimeout)),
        ("408", () => Status(HttpStatusCode.RequestTimeout)),
        ("400", () => Status(HttpStatusCode.BadRequest)),
        ("401", () => Status(HttpStatusCode.Unauthorized)),
        ("403", () => Status(HttpStatusCode.Forbidden)),
        ("403 retry-after", () => Status(HttpStatusCode.Forbidden, configure: r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero))),
        ("204 where 200 expected", () => Status(HttpStatusCode.NoContent)),
        ("200 where 204 expected", () => Status(HttpStatusCode.OK)),
        ("502", () => Status(HttpStatusCode.BadGateway)),
    ];

    private static readonly (string Name, Func<Exception> Make)[] TransportFailures =
    [
        ("socket timed out", () => new HttpRequestException("x", new SocketException((int)SocketError.TimedOut))),
        ("socket reset", () => new HttpRequestException("x", new IOException("y", new SocketException((int)SocketError.ConnectionReset)))),
        ("connection refused", () => new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused))),
        ("host not found", () => new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound))),
        ("plain http failure", () => new HttpRequestException("x")),
        ("client timeout", () => new TaskCanceledException("t", new TimeoutException())),
    ];

    private static async Task<Exception?> Run(string operation, NotificationHubClient? sdk, NotificationHubRestClient? own)
    {
        var ct = TestContext.Current.CancellationToken;
        try
        {
            switch (operation)
            {
                case "put" when sdk is not null:
                    await sdk.CreateOrUpdateInstallationAsync(new Installation { InstallationId = "abc", Platform = NotificationPlatform.FcmV1, PushChannel = "c", Tags = ["user:1"] }, ct);
                    break;
                case "put":
                    await own!.CreateOrUpdateInstallationAsync(new NotificationHubInstallation("abc", NotificationHubPlatform.FcmV1, "c", ["user:1"]), ct);
                    break;
                case "get" when sdk is not null:
                    await sdk.GetInstallationAsync("abc", ct);
                    break;
                case "get":
                    await own!.GetInstallationAsync("abc", ct);
                    break;
                case "delete" when sdk is not null:
                    await sdk.DeleteInstallationAsync("abc", ct);
                    break;
                case "delete":
                    await own!.DeleteInstallationAsync("abc", ct);
                    break;
                case "list" when sdk is not null:
                    await sdk.GetRegistrationsByTagAsync("user:1", string.Empty, 100, ct);
                    break;
                case "list":
                    await own!.GetRegistrationsByTagAsync("user:1", string.Empty, 100, ct);
                    break;
                case "send" when sdk is not null:
                    await sdk.SendNotificationAsync(new FcmV1Notification("{}"), "user:1", ct);
                    break;
                default:
                    await own!.SendNotificationAsync(NotificationHubNotification.FcmV1("{}"), "user:1", ct);
                    break;
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string Classify(Exception? ex) => ex switch
    {
        null => "success",
        MessagingEntityNotFoundException => "not-found",
        MessagingException m => $"failure transient={m.IsTransient}",
        NotificationHubException h when h.IsEntityNotFound => "not-found",
        NotificationHubException h => $"failure transient={h.IsTransient}",
        _ => ex.GetType().Name,
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task FailureStatuses_SameClassification_SameAttemptCount(string operation)
    {
        foreach (var (name, make) in Failures)
        {
            var sdkHandler = new RecordingHandler(Always(make));
            var ownHandler = new RecordingHandler(Always(make));

            var sdkError = await Run(operation, Sdk(sdkHandler), null);
            var ownError = await Run(operation, null, Own(ownHandler));

            var label = $"{operation} {name}";
            Classify(ownError).Should().Be(Classify(sdkError), label);
            ownHandler.Requests.Should().HaveCount(sdkHandler.Requests.Count, $"{label}: attempts");
            Log($"{label}: {Classify(sdkError)}, attempts {sdkHandler.Requests.Count}");
            _compared++;
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task TransportFailures_SameClassification_SameAttemptCount(string operation)
    {
        foreach (var (name, make) in TransportFailures)
        {
            var sdkHandler = new RecordingHandler((_, _) => throw make());
            var ownHandler = new RecordingHandler((_, _) => throw make());

            var sdkError = await Run(operation, Sdk(sdkHandler), null);
            var ownError = await Run(operation, null, Own(ownHandler));

            var label = $"{operation} {name}";
            Classify(ownError).Should().Be(Classify(sdkError), label);
            ownHandler.Requests.Should().HaveCount(sdkHandler.Requests.Count, $"{label}: attempts");
            Log($"{label}: {Classify(sdkError)}, attempts {sdkHandler.Requests.Count}");
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task RecoveryAfterOneTransientFailure_SameAttempts(string operation)
    {
        HttpResponseMessage Respond(HttpRequestMessage _, int attempt) => attempt == 1
            ? Status(HttpStatusCode.ServiceUnavailable)
            : operation switch
            {
                "delete" => Status(HttpStatusCode.NoContent),
                "get" => Status(HttpStatusCode.OK, "{\"installationId\":\"abc\",\"platform\":\"fcmV1\"}"),
                "list" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed()) },
                _ => Status(HttpStatusCode.OK),
            };

        var sdkHandler = new RecordingHandler(Respond);
        var ownHandler = new RecordingHandler(Respond);

        var sdkError = await Run(operation, Sdk(sdkHandler), null);
        var ownError = await Run(operation, null, Own(ownHandler));

        Classify(ownError).Should().Be(Classify(sdkError));
        ownHandler.Requests.Should().HaveCount(sdkHandler.Requests.Count);
        if (sdkHandler.Requests.Count == 2)
        {
            ownHandler.Requests[0].Headers.Single(h => h.Key == "TrackingId").Value
                .Should().NotBe(ownHandler.Requests[1].Headers.Single(h => h.Key == "TrackingId").Value, "each attempt is a fresh request");
        }

        Log($"{operation} 503 then success: {Classify(sdkError)}, attempts {sdkHandler.Requests.Count}");
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task PreCancelledToken_SameOutcome(string operation)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sdkHandler = new RecordingHandler(Always(() => Status(HttpStatusCode.OK)));
        var ownHandler = new RecordingHandler(Always(() => Status(HttpStatusCode.OK)));

        Exception? sdkError = null;
        Exception? ownError = null;
        try
        {
            switch (operation)
            {
                case "put": await Sdk(sdkHandler).CreateOrUpdateInstallationAsync(new Installation { InstallationId = "abc", Platform = NotificationPlatform.FcmV1, PushChannel = "c" }, cts.Token); break;
                case "get": await Sdk(sdkHandler).GetInstallationAsync("abc", cts.Token); break;
                case "delete": await Sdk(sdkHandler).DeleteInstallationAsync("abc", cts.Token); break;
                case "list": await Sdk(sdkHandler).GetRegistrationsByTagAsync("user:1", string.Empty, 100, cts.Token); break;
                default: await Sdk(sdkHandler).SendNotificationAsync(new FcmV1Notification("{}"), "user:1", cts.Token); break;
            }
        }
        catch (Exception ex)
        {
            sdkError = ex;
        }

        try
        {
            var own = Own(ownHandler);
            switch (operation)
            {
                case "put": await own.CreateOrUpdateInstallationAsync(new NotificationHubInstallation("abc", NotificationHubPlatform.FcmV1, "c", null), cts.Token); break;
                case "get": await own.GetInstallationAsync("abc", cts.Token); break;
                case "delete": await own.DeleteInstallationAsync("abc", cts.Token); break;
                case "list": await own.GetRegistrationsByTagAsync("user:1", string.Empty, 100, cts.Token); break;
                default: await own.SendNotificationAsync(NotificationHubNotification.FcmV1("{}"), "user:1", cts.Token); break;
            }
        }
        catch (Exception ex)
        {
            ownError = ex;
        }

        ownError?.GetType().Should().Be(sdkError?.GetType());
        (ownError is null).Should().Be(sdkError is null);
        ownHandler.Requests.Should().HaveCount(sdkHandler.Requests.Count);
        Log($"{operation} pre-cancelled: {sdkError?.GetType().Name}, attempts {sdkHandler.Requests.Count}");
    }

    [Fact]
    public void UserAgent_IsTheSdks()
    {
        var sdkHandler = new RecordingHandler(Always(() => Status(HttpStatusCode.OK)));
        _ = Sdk(sdkHandler);
        NotificationHubRestClient.UserAgent.Should().StartWith("NHub/2020-06 (api-origin=DotNetSdk;");
        _compared.Should().Be(0);
    }
}
