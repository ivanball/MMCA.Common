using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using MMCA.Common.Azure.Notifications;
using MMCA.Common.Infrastructure.Notifications.Push;

namespace MMCA.Common.Azure.Tests.Notifications;

/// <summary>
/// Pins the exact HTTP traffic of the own Notification Hubs REST client (ADR-044). Every expected
/// value here (URLs, header names, order and values, body bytes, the full Shared Access Signature on
/// a fixed clock, the parsed registration tags, the failure classification and the attempt counts)
/// was captured from the Microsoft.Azure.NotificationHubs 4.2.0 SDK by a differential test that drove
/// the SDK and this client against the same recording handler with zero differences, before the SDK
/// was removed. A change here changes what the production hub receives.
/// </summary>
public sealed class NotificationHubRestClientRequestShapeTests
{
    private const string HubName = "mmca-hub";
    private const string ConnectionString =
        "Endpoint=sb://mmca-ns.servicebus.windows.net/;SharedAccessKeyName=DefaultFullSharedAccessSignature;SharedAccessKey=c2VjcmV0LWtleS1mb3ItdGVzdHMtb25seQ==";

    private const string SasPrefix = "SharedAccessSignature sr=http%3a%2f%2fmmca-ns.servicebus.windows.net%2fmmca-hub%2f";
    private const string SasKeyName = "&skn=DefaultFullSharedAccessSignature";
    private const string TagsSignature = "tags%2fuser%3a42%2fregistrations%2f&sig=TCqHqf8KaH%2btJVb0b8b91iJdyVZx9Giapf%2bbhdMW4sA%3d&se=1795548176";

    // An id that exercises path escaping: a space, '?', '#', an escaped 'A', a slash, U+2028, U+00E9
    // and an astral emoji.
    private static readonly string TrickyId = "id with space?#%41/" + (char)0x2028 + (char)0xE9 + char.ConvertFromUtf32(0x1F600);

    private static readonly NotificationHubConnection Connection = NotificationHubConnection.Parse(ConnectionString, HubName);

    private sealed record Recorded(
        string Method,
        string AbsoluteUri,
        IReadOnlyList<KeyValuePair<string, string>> Headers,
        IReadOnlyList<KeyValuePair<string, string>> ContentHeaders,
        byte[]? Body);

    private sealed class RecordingHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        public Func<Exception>? Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[]? body = null;
            List<KeyValuePair<string, string>> contentHeaders = [];
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                _ = request.Content.Headers.ContentLength;
                contentHeaders.AddRange(request.Content.Headers.Select(h => KeyValuePair.Create(h.Key, string.Join(", ", h.Value))));
            }

            Requests.Add(new Recorded(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                [.. request.Headers.Select(h => KeyValuePair.Create(h.Key, string.Join(" ", h.Value)))],
                contentHeaders,
                body));

            if (Throw is not null)
            {
                throw Throw();
            }

            var response = respond(Requests.Count);
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>A client whose clock makes every token expire at <paramref name="expiry"/> (Unix seconds).</summary>
    private static NotificationHubRestClient Client(RecordingHandler handler, long expiry = 1795548177) =>
        new(
            new HttpClient(handler),
            Connection,
            new FixedClock(DateTimeOffset.FromUnixTimeSeconds(expiry) - NotificationHubConnection.TokenTimeToLive),
            new NotificationHubRetryPolicy(3, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(5)));

    private static HttpResponseMessage Status(HttpStatusCode code, string? body = null)
    {
        var response = new HttpResponseMessage(code);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return response;
    }

    private static void AssertHeaders(Recorded request, string authorization, params (string Name, string Value)[] between)
    {
        request.Headers.Select(h => h.Key).Should().Equal(
            ["TrackingId", "Authorization", .. between.Select(h => h.Name), "User-Agent"],
            "the header set and order the SDK sent");
        Guid.TryParse(request.Headers[0].Value, out _).Should().BeTrue("TrackingId is a fresh GUID per attempt");
        request.Headers[1].Value.Should().Be(authorization);
        foreach (var (name, value) in between)
        {
            request.Headers.Single(h => h.Key == name).Value.Should().Be(value);
        }

        request.Headers[^1].Value.Should().Be(NotificationHubRestClient.UserAgent);
    }

    [Fact]
    public void UserAgent_IsTheSdkString() =>
        NotificationHubRestClient.UserAgent.Should().Be(
            $"NHub/2020-06 (api-origin=DotNetSdk;os={Environment.OSVersion.Platform};os-version={Environment.OSVersion.Version})");

    public static TheoryData<string, string, string, string, long, string, string> Upserts => new()
    {
        {
            "fcm", "3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d", "dGVzdA:APA91bH_fcm-registration-token-0123456789", "user:42", 1795548175,
            SasPrefix + "installations%2f3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d%2f&sig=vm8egU67fuvKzxkJJAKuRdZG75pUr2q9%2fcvI93CsD40%3d&se=1795548175" + SasKeyName,
            "{\"installationId\":\"3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d\",\"userId\":null,\"pushChannel\":\"dGVzdA:APA91bH_fcm-registration-token-0123456789\",\"pushChannelExpired\":null,\"platform\":\"fcmV1\",\"expirationTime\":null,\"tags\":[\"user:42\"]}"
        },
        {
            "apns", "94018932-9656-48dd-aea1-0c2253b4e093", "62BCD3484834B7E2DC56733AC5509259230CC1BA82BA1AE7D06E91908A05A8FA", "user:7", 1795548175,
            SasPrefix + "installations%2f94018932-9656-48dd-aea1-0c2253b4e093%2f&sig=xF%2b%2bAD8%2b82N%2bmXdh1nY%2fOi%2fa9CssR%2fRNoDeONjMKkOQ%3d&se=1795548175" + SasKeyName,
            "{\"installationId\":\"94018932-9656-48dd-aea1-0c2253b4e093\",\"userId\":null,\"pushChannel\":\"62BCD3484834B7E2DC56733AC5509259230CC1BA82BA1AE7D06E91908A05A8FA\",\"pushChannelExpired\":null,\"platform\":\"apns\",\"expirationTime\":null,\"tags\":[\"user:7\"]}"
        },
    };

    [Theory]
    [MemberData(nameof(Upserts))]
    public async Task CreateOrUpdateInstallation_SendsTheSdkRequest(string platform, string id, string channel, string tag, long expiry, string authorization, string body)
    {
        var handler = new RecordingHandler(_ => Status(HttpStatusCode.OK));

        await Client(handler, expiry).CreateOrUpdateInstallationAsync(
            new NotificationHubInstallation(id, platform == "fcm" ? NotificationHubPlatform.FcmV1 : NotificationHubPlatform.Apns, channel, [tag]),
            TestContext.Current.CancellationToken);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be("PUT");
        request.AbsoluteUri.Should().Be($"https://mmca-ns.servicebus.windows.net/mmca-hub/installations/{id}?api-version=2020-06");
        AssertHeaders(request, authorization);
        request.ContentHeaders.Should().Equal(
            KeyValuePair.Create("Content-Type", "application/json; charset=utf-8"),
            KeyValuePair.Create("Content-Length", Encoding.UTF8.GetByteCount(body).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Encoding.UTF8.GetString(request.Body!).Should().Be(body);
    }

    [Fact]
    public async Task CreateOrUpdateInstallation_EscapesPathAndJsonExactlyAsTheSdk()
    {
        // Control characters, quotes, backslashes, U+0085/U+2028/U+2029 (escaped by the SDK's JSON
        // writer), DEL, HTML characters, U+00E9 and an emoji (all written raw).
        var channel = "chan\"nel\\with\ttab\n\r\f\b" + (char)0x01 + (char)0x1F + (char)0x7F + (char)0x85 + (char)0x2028 + (char)0x2029
            + "'<>&" + (char)0xE9 + char.ConvertFromUtf32(0x1F600);
        var tags = new[] { "user:1", "tag \"q\" \\ " + (char)0x2029 };
        var handler = new RecordingHandler(_ => Status(HttpStatusCode.OK));

        await Client(handler, 1795548175).CreateOrUpdateInstallationAsync(
            new NotificationHubInstallation(TrickyId, NotificationHubPlatform.FcmV1, channel, tags),
            TestContext.Current.CancellationToken);

        var request = handler.Requests.Single();
        request.AbsoluteUri.Should().Be("https://mmca-ns.servicebus.windows.net/mmca-hub/installations/id%20with%20space%3F%23A/%E2%80%A8%C3%A9%F0%9F%98%80?api-version=2020-06");
        AssertHeaders(
            request,
            SasPrefix + "installations%2fid%2520with%2520space%253f%2523a%2f%25e2%2580%25a8%25c3%25a9%25f0%259f%2598%2580%2f&sig=P6VSgi%2fqaysuRIMEtIaDQkMtUXj70%2b8vCPbt8rOoCJ4%3d&se=1795548175" + SasKeyName);
        request.Body.Should().Equal(Convert.FromBase64String(
            "eyJpbnN0YWxsYXRpb25JZCI6ImlkIHdpdGggc3BhY2U/IyU0MS9cdTIwMjjDqfCfmIAiLCJ1c2VySWQiOm51bGwsInB1c2hDaGFubmVsIjoiY2hhblwibmVsXFx3aXRoXHR0YWJcblxyXGZcYlx1MDAwMVx1MDAxZn9cdTAwODVcdTIwMjhcdTIwMjknPD4mw6nwn5iAIiwicHVzaENoYW5uZWxFeHBpcmVkIjpudWxsLCJwbGF0Zm9ybSI6ImZjbVYxIiwiZXhwaXJhdGlvblRpbWUiOm51bGwsInRhZ3MiOlsidXNlcjoxIiwidGFnIFwicVwiIFxcIFx1MjAyOSJdfQ=="));
    }

    [Fact]
    public async Task CreateOrUpdateInstallation_WithoutTags_OmitsTheTagsMember()
    {
        var handler = new RecordingHandler(_ => Status(HttpStatusCode.OK));

        await Client(handler).CreateOrUpdateInstallationAsync(
            new NotificationHubInstallation("abc", NotificationHubPlatform.Apns, null, null),
            TestContext.Current.CancellationToken);

        Encoding.UTF8.GetString(handler.Requests.Single().Body!).Should().Be(
            "{\"installationId\":\"abc\",\"userId\":null,\"pushChannel\":null,\"pushChannelExpired\":null,\"platform\":\"apns\",\"expirationTime\":null}");
    }

    [Theory]
    [InlineData("3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d", "https://mmca-ns.servicebus.windows.net/mmca-hub/installations/3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d?api-version=2020-06", "installations%2f3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d%2f&sig=V2J7o%2b47AH6EoKGuqVRk11oB95RU4QjJ%2fzNNec8dXVk%3d&se=1795548177")]
    [InlineData("abc", "https://mmca-ns.servicebus.windows.net/mmca-hub/installations/abc?api-version=2020-06", "installations%2fabc%2f&sig=%2fufvhouAdAzNENlS7PYB20lwQi6FNoiylhwXhXomR7A%3d&se=1795548177")]
    public async Task GetInstallation_SendsTheSdkRequest(string id, string uri, string signature)
    {
        var handler = new RecordingHandler(_ => Status(HttpStatusCode.OK, "{\"installationId\":\"x\",\"pushChannel\":\"p\",\"platform\":\"fcmV1\",\"tags\":[\"user:42\"]}"));

        var installation = await Client(handler, 1795548177).GetInstallationAsync(id, TestContext.Current.CancellationToken);

        var request = handler.Requests.Single();
        request.Method.Should().Be("GET");
        request.AbsoluteUri.Should().Be(uri);
        AssertHeaders(request, SasPrefix + signature + SasKeyName);
        request.ContentHeaders.Should().BeEmpty();
        request.Body.Should().BeNull();
        installation.Should().NotBeNull();
        installation!.PushChannel.Should().Be("p");
        installation.Tags.Should().Equal("user:42");
    }

    [Theory]
    [InlineData("3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d", "https://mmca-ns.servicebus.windows.net/mmca-hub/installations/3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d?api-version=2020-06", "installations%2f3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d%2f&sig=mnxJ9fo9UUBEsQlFFv5jDBAaFq%2fhxm1OSBBpS06RP6c%3d&se=1795548173")]
    [InlineData("id with space?#%41/", "https://mmca-ns.servicebus.windows.net/mmca-hub/installations/id%20with%20space%3F%23A/?api-version=2020-06", "installations%2fid%2520with%2520space%253f%2523a%2f&sig=ZFqtjjxbZDfC%2fsMkG%2bNrjP0V37rpOmSu9fQbczvAwCU%3d&se=1795548173")]
    public async Task DeleteInstallation_SendsTheSdkRequest(string id, string uri, string signature)
    {
        var handler = new RecordingHandler(_ => Status(HttpStatusCode.NoContent));

        await Client(handler, 1795548173).DeleteInstallationAsync(id, TestContext.Current.CancellationToken);

        var request = handler.Requests.Single();
        request.Method.Should().Be("DELETE");
        request.AbsoluteUri.Should().Be(uri);
        AssertHeaders(request, SasPrefix + signature + SasKeyName);
        request.Body.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "https://mmca-ns.servicebus.windows.net/mmca-hub/tags/user:42/registrations?api-version=2020-06&$top=100")]
    [InlineData("", "https://mmca-ns.servicebus.windows.net/mmca-hub/tags/user:42/registrations?api-version=2020-06&$top=100")]
    [InlineData("next+page/token==", "https://mmca-ns.servicebus.windows.net/mmca-hub/tags/user:42/registrations?api-version=2020-06&$top=100&continuationtoken=next+page/token==")]
    public async Task GetRegistrationsByTag_SendsTheSdkRequest(string? continuationToken, string uri)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(NotificationHubFeeds.Feed()) });

        await Client(handler, 1795548176).GetRegistrationsByTagAsync("user:42", continuationToken, 100, TestContext.Current.CancellationToken);

        var request = handler.Requests.Single();
        request.Method.Should().Be("GET");
        request.AbsoluteUri.Should().Be(uri);
        AssertHeaders(request, SasPrefix + TagsSignature + SasKeyName);
    }

    public static TheoryData<string, string?, string[][]> Feeds => new()
    {
        { NotificationHubFeeds.RecordedAppleFeed, null, [["tag1"], ["tag1"]] },

        // "Template Name" breaks the hub's tag grammar (a space), so the SDK read that registration as tagless.
        { NotificationHubFeeds.RecordedWindowsFeed, "next+page/token==", [["$InstallationId:{ef4ffd64-8f20-4d6e-873c-7308b28d6a04}"], []] },
        {
            NotificationHubFeeds.EdgeCaseFeed, "page-3",
            [
                ["$InstallationId:{3f0c2f9c-1e8b-4b6c-9d3a-5e7f0a1b2c3d}", "user:42"],
                ["$installationid:{SECOND}", "user:42"],
                [],
                [],
                ["$UserId:{u@x.com}", "user:42", new string('x', 120)],
                [],
                [],
                ["$InstallationId:{c}", "tmpl", "user:42"],
                ["$InstallationId:{d=e}", "user:42"],
            ]
        },
        { NotificationHubFeeds.Feed(), null, [] },
    };

    [Theory]
    [MemberData(nameof(Feeds))]
    public async Task GetRegistrationsByTag_ParsesTheTagsAsTheSdkDid(string feed, string? nextToken, string[][] expected)
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(feed, Encoding.UTF8, "application/atom+xml") };
            if (nextToken is not null)
            {
                response.Headers.Add("x-ms-continuationtoken", nextToken);
            }

            return response;
        });

        var page = await Client(handler).GetRegistrationsByTagAsync("user:42", null, 100, TestContext.Current.CancellationToken);

        page.ContinuationToken.Should().Be(nextToken);
        page.RegistrationTags.Select(tags => tags.Order(StringComparer.Ordinal).ToArray()).Should().BeEquivalentTo(
            expected.Select(tags => tags.Order(StringComparer.Ordinal).ToArray()),
            options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData("fcm", "fcmv1")]
    [InlineData("apns", "apple")]
    public async Task SendNotification_SendsTheSdkRequest(string platform, string format)
    {
        var tagExpression = NativePushUserTags.BuildUserTagExpressions(Enumerable.Range(1, 25)).First();
        var metadata = new Dictionary<string, string> { ["route"] = "/events/1?x=\"y\"", ["emoji"] = "caf" + (char)0xE9 + " " + char.ConvertFromUtf32(0x1F600) };
        var payload = platform == "fcm"
            ? NativePushPayloads.BuildFcmV1Payload("Title \"q\"", "Body <b>&", metadata)
            : NativePushPayloads.BuildApnsPayload("Title \"q\"", "Body <b>&", metadata);
        var handler = new RecordingHandler(_ => Status(HttpStatusCode.Created));

        await Client(handler, 1795548177).SendNotificationAsync(
            platform == "fcm" ? NotificationHubNotification.FcmV1(payload) : NotificationHubNotification.Apple(payload),
            tagExpression,
            TestContext.Current.CancellationToken);

        var request = handler.Requests.Single();
        request.Method.Should().Be("POST");
        request.AbsoluteUri.Should().Be("https://mmca-ns.servicebus.windows.net/mmca-hub/messages?api-version=2020-06");
        tagExpression.Should().Be(string.Join(" || ", Enumerable.Range(1, 20).Select(i => "user:" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        AssertHeaders(
            request,
            SasPrefix + "messages%2f&sig=%2fAuSxQbjDfEoiJApNUo6KMS9FZPFJx89m3i1jsd%2fdi8%3d&se=1795548177" + SasKeyName,
            ("ServiceBusNotification-Tags", tagExpression),
            ("ServiceBusNotification-Format", format));
        request.ContentHeaders.Should().Equal(
            KeyValuePair.Create("Content-Type", "application/json"),
            KeyValuePair.Create("Content-Length", Encoding.UTF8.GetByteCount(payload).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        request.Body.Should().Equal(Encoding.UTF8.GetBytes(payload));
    }

    [Fact]
    public async Task SendNotification_WithAnEmptyTagExpression_SendsNoTagsHeader()
    {
        var handler = new RecordingHandler(_ => Status(HttpStatusCode.OK));

        await Client(handler).SendNotificationAsync(NotificationHubNotification.Apple("{}"), string.Empty, TestContext.Current.CancellationToken);

        handler.Requests.Single().Headers.Select(h => h.Key).Should().Equal("TrackingId", "Authorization", "ServiceBusNotification-Format", "User-Agent");
    }

    public static TheoryData<string> Operations => new() { "put", "get", "delete", "list", "send" };

    private static async Task<Exception?> Run(string operation, NotificationHubRestClient client, CancellationToken cancellationToken)
    {
        try
        {
            switch (operation)
            {
                case "put":
                    await client.CreateOrUpdateInstallationAsync(new NotificationHubInstallation("abc", NotificationHubPlatform.FcmV1, "c", ["user:1"]), cancellationToken);
                    break;
                case "get":
                    await client.GetInstallationAsync("abc", cancellationToken);
                    break;
                case "delete":
                    await client.DeleteInstallationAsync("abc", cancellationToken);
                    break;
                case "list":
                    await client.GetRegistrationsByTagAsync("user:1", string.Empty, 100, cancellationToken);
                    break;
                default:
                    await client.SendNotificationAsync(NotificationHubNotification.FcmV1("{}"), "user:1", cancellationToken);
                    break;
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string Classify(Exception? exception) => exception switch
    {
        null => "success",
        NotificationHubException { IsEntityNotFound: true } => "not-found",
        NotificationHubException hub => hub.IsTransient ? "transient" : "permanent",
        _ => exception.GetType().Name,
    };

    private static HttpResponseMessage WithRetryAfter(HttpStatusCode code)
    {
        var response = Status(code);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    // Captured from the SDK: the classification of each failure and how many attempts it got. Every
    // operation but the registrations listing retries a transient failure three times.
    public static TheoryData<string, string, string, int> FailureTable
    {
        get
        {
            var data = new TheoryData<string, string, string, int>();
            (string Status, string Outcome, int Attempts)[] statuses =
            [
                ("404", "not-found", 1),
                ("409", "permanent", 1),
                ("410", "permanent", 1),
                ("400", "permanent", 1),
                ("401", "permanent", 1),
                ("403", "permanent", 1),
                ("502", "permanent", 1),
                ("429", "transient", 4),
                ("429 retry-after", "transient", 4),
                ("403 retry-after", "transient", 4),
                ("500", "transient", 4),
                ("503", "transient", 4),
                ("504", "transient", 4),
                ("408", "transient", 4),
            ];
            foreach (var operation in new[] { "put", "get", "delete", "list", "send" })
            {
                foreach (var (status, outcome, attempts) in statuses)
                {
                    data.Add(operation, status, outcome, operation == "list" ? 1 : attempts);
                }
            }

            data.Add("delete", "200", "permanent", 1);
            data.Add("put", "204", "not-found", 1);
            data.Add("get", "204", "not-found", 1);
            data.Add("send", "204", "not-found", 1);
            data.Add("list", "204", "not-found", 1);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FailureTable))]
    public async Task FailureStatuses_AreClassifiedAndRetriedAsTheSdkDid(string operation, string status, string outcome, int attempts)
    {
        Func<HttpResponseMessage> make = status switch
        {
            "429 retry-after" => () => WithRetryAfter(HttpStatusCode.TooManyRequests),
            "403 retry-after" => () => WithRetryAfter(HttpStatusCode.Forbidden),
            _ => () => Status((HttpStatusCode)int.Parse(status, System.Globalization.CultureInfo.InvariantCulture), "<Error><Code>1</Code><Detail>detail</Detail></Error>"),
        };
        var handler = new RecordingHandler(_ => make());

        var error = await Run(operation, Client(handler), TestContext.Current.CancellationToken);

        Classify(error).Should().Be(outcome);
        handler.Requests.Should().HaveCount(attempts);
    }

    public static TheoryData<string, string, string, int> TransportTable
    {
        get
        {
            var data = new TheoryData<string, string, string, int>();
            (string Failure, string Outcome, int Attempts)[] failures =
            [
                ("socket timed out", "transient", 4),
                ("socket reset", "transient", 4),
                ("connection refused", "permanent", 1),
                ("host not found", "permanent", 1),
                ("plain http failure", "permanent", 1),
                ("client timeout", nameof(TaskCanceledException), 4),
            ];
            foreach (var operation in new[] { "put", "get", "delete", "list", "send" })
            {
                foreach (var (failure, outcome, attempts) in failures)
                {
                    data.Add(operation, failure, outcome, operation == "list" ? 1 : attempts);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(TransportTable))]
    public async Task TransportFailures_AreClassifiedAndRetriedAsTheSdkDid(string operation, string failure, string outcome, int attempts)
    {
        Func<Exception> make = failure switch
        {
            "socket timed out" => () => new HttpRequestException("x", new SocketException((int)SocketError.TimedOut)),
            "socket reset" => () => new HttpRequestException("x", new IOException("y", new SocketException((int)SocketError.ConnectionReset))),
            "connection refused" => () => new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)),
            "host not found" => () => new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)),
            "plain http failure" => () => new HttpRequestException("x"),
            _ => () => new TaskCanceledException("timed out", new TimeoutException()),
        };
        var handler = new RecordingHandler(_ => Status(HttpStatusCode.OK)) { Throw = make };

        var error = await Run(operation, Client(handler), TestContext.Current.CancellationToken);

        Classify(error).Should().Be(outcome);
        handler.Requests.Should().HaveCount(attempts);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task OneTransientFailure_ThenSuccess_RetriesWithAFreshRequest(string operation)
    {
        var handler = new RecordingHandler(attempt => attempt == 1
            ? Status(HttpStatusCode.ServiceUnavailable)
            : operation switch
            {
                "delete" => Status(HttpStatusCode.NoContent),
                "get" => Status(HttpStatusCode.OK, "{\"installationId\":\"abc\",\"platform\":\"fcmV1\"}"),
                "list" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(NotificationHubFeeds.Feed()) },
                _ => Status(HttpStatusCode.OK),
            });

        var error = await Run(operation, Client(handler), TestContext.Current.CancellationToken);

        if (operation == "list")
        {
            Classify(error).Should().Be("transient", "the SDK never retried the registrations listing");
            handler.Requests.Should().ContainSingle();
            return;
        }

        error.Should().BeNull();
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Headers[0].Value.Should().NotBe(handler.Requests[1].Headers[0].Value, "each attempt carries a new TrackingId");
    }

    [Theory]
    [InlineData("put", 0)]
    [InlineData("get", 0)]
    [InlineData("delete", 0)]
    [InlineData("send", 0)]
    [InlineData("list", 1)]
    public async Task APreCancelledToken_EndsAsTheSdkDid(string operation, int attempts)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new RecordingHandler(_ => Status(HttpStatusCode.OK));

        var error = await Run(operation, Client(handler), cts.Token);

        error.Should().BeOfType<TaskCanceledException>();
        handler.Requests.Should().HaveCount(attempts);
    }
}
