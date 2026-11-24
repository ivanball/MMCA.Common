using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using AwesomeAssertions;
using MMCA.Common.Azure.Notifications;

namespace MMCA.Common.Azure.Tests.Notifications;

/// <summary>
/// The connection string parsing, the Shared Access Signature algorithm, the installation read-back
/// and the retry schedule of the own Notification Hubs client (ADR-044), each as the
/// Microsoft.Azure.NotificationHubs 4.2.0 SDK implemented it.
/// </summary>
public sealed class NotificationHubConnectionTests
{
    private const string Key = "c2VjcmV0LWtleS1mb3ItdGVzdHMtb25seQ==";

    [Theory]
    [InlineData("Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=Full;SharedAccessKey=" + Key)]
    [InlineData("endpoint=sb://ns.servicebus.windows.net;sharedaccesskeyname=Full;SHAREDACCESSKEY=" + Key)]
    [InlineData("SharedAccessKey=" + Key + ";SharedAccessKeyName=Full;Endpoint=sb://ns.servicebus.windows.net/")]
    public void Parse_ReadsTheThreeKeys_CaseInsensitively_InAnyOrder(string connectionString)
    {
        var connection = NotificationHubConnection.Parse(connectionString, "hub");

        connection.Endpoint.Host.Should().Be("ns.servicebus.windows.net");
        connection.KeyName.Should().Be("Full");
        connection.HubName.Should().Be("hub");
        connection.CreateRequestUriBuilder().Uri.AbsoluteUri.Should().Be("https://ns.servicebus.windows.net/hub/?api-version=2020-06");
    }

    [Theory]
    [InlineData("not-a-connection-string")]
    [InlineData("Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=Full")]
    [InlineData("Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=Full;SharedAccessKey=")]
    [InlineData("Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=Full;SharedAccessKeyName=Other;SharedAccessKey=k")]
    public void Parse_RejectsAMalformedConnectionString(string connectionString)
    {
        var parse = () => NotificationHubConnection.Parse(connectionString, "hub");

        parse.Should().Throw<Exception>().Which.Should().Match<Exception>(e => e is FormatException || e is UriFormatException);
    }

    [Fact]
    public void CreateSharedAccessSignature_IsTheHmacOfTheLowerCasedHttpAudienceAndTheExpiry()
    {
        var connection = NotificationHubConnection.Parse($"Endpoint=sb://NS.servicebus.windows.net/;SharedAccessKeyName=Full;SharedAccessKey={Key}", "Hub");
        var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

        var token = connection.CreateSharedAccessSignature(new Uri("https://ns.servicebus.windows.net/Hub/installations/AbC?api-version=2020-06"), now);

        // Independent reconstruction: http scheme, no query, trailing slash, lower-cased, URL-encoded;
        // the expiry twenty minutes out in Unix seconds; HMAC-SHA256 under the UTF-8 key bytes.
        const string audience = "http%3a%2f%2fns.servicebus.windows.net%2fhub%2finstallations%2fabc%2f";
        var expiry = (now + TimeSpan.FromMinutes(20)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Key));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(audience + "\n" + expiry)));
        token.Should().Be($"SharedAccessSignature sr={audience}&sig={HttpUtility.UrlEncode(signature)}&se={expiry}&skn=Full");
    }

    [Theory]
    [InlineData("{\"installationId\":\"x\",\"pushChannel\":\"p\",\"platform\":\"apns\",\"tags\":[\"user:42\",\"Other\"]}", "p", new[] { "user:42", "Other" })]
    [InlineData("{\"installationId\":\"x\",\"PUSHCHANNEL\":\"upper\",\"platform\":\"gcm\"}", "upper", null)]
    [InlineData("{\"installationId\":\"x\",\"pushChannel\":null,\"platform\":\"apns\",\"tags\":null}", null, null)]
    [InlineData("{\"installationId\":\"x\",\"pushChannel\":\"p\",\"platform\":\"fcmV1\",\"tags\":[\"user:1\"],\"templates\":{\"t\":{\"body\":\"{}\"}}}", "p", new[] { "user:1" })]
    public void ReadInstallation_ReadsThePushChannelAndTags(string json, string? pushChannel, string[]? tags)
    {
        var installation = NotificationHubWireFormat.ReadInstallation(json);

        installation.Should().NotBeNull();
        installation!.PushChannel.Should().Be(pushChannel);
        if (tags is null)
        {
            installation.Tags.Should().BeNull();
        }
        else
        {
            installation.Tags.Should().Equal(tags);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public void ReadInstallation_AnEmptyOrNullBody_IsNoInstallation(string json) =>
        NotificationHubWireFormat.ReadInstallation(json).Should().BeNull();

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"tags\":\"user:1\"}")]
    [InlineData("{\"pushChannel\":5}")]
    [InlineData("not json")]
    public void ReadInstallation_AMalformedBody_Throws(string json)
    {
        var read = () => NotificationHubWireFormat.ReadInstallation(json);

        read.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData(1, 2.0)]
    [InlineData(2, 4.0)]
    [InlineData(3, 8.0)]
    public void DefaultRetry_BacksOffExponentially_WithUpToEightPercentJitter(int attempt, double baseSeconds)
    {
        var delay = NotificationHubRetryPolicy.Default.CalculateRetryDelay(Transient(), attempt);

        delay.Should().NotBeNull();
        delay!.Value.TotalSeconds.Should().BeInRange(baseSeconds, baseSeconds + 0.08);
    }

    [Fact]
    public void DefaultRetry_StopsAfterThreeRetries() =>
        NotificationHubRetryPolicy.Default.CalculateRetryDelay(Transient(), 4).Should().BeNull();

    [Fact]
    public void DefaultRetry_HonorsTheHubsRetryAfter_CappedAtOneMinute()
    {
        NotificationHubRetryPolicy.Default.CalculateRetryDelay(Throttled(TimeSpan.FromSeconds(30)), 1).Should().Be(TimeSpan.FromSeconds(30));
        NotificationHubRetryPolicy.Default.CalculateRetryDelay(Throttled(TimeSpan.FromMinutes(5)), 1).Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void DefaultRetry_RetriesTimeoutsAndTransientFailures_NeverPermanentOnesOrTheCallersCancellation()
    {
        var policy = NotificationHubRetryPolicy.Default;

        policy.CalculateRetryDelay(new TaskCanceledException("t", new TimeoutException()), 1).Should().NotBeNull("an attempt that hit the client timeout");
        policy.CalculateRetryDelay(new TimeoutException(), 1).Should().NotBeNull();
        policy.CalculateRetryDelay(new OperationCanceledException(), 1).Should().BeNull("the caller's own cancellation");
        policy.CalculateRetryDelay(new NotificationHubException("x", HttpStatusCode.Conflict, isTransient: false), 1).Should().BeNull();
        policy.CalculateRetryDelay(new InvalidOperationException(), 1).Should().BeNull();
    }

    private static NotificationHubException Transient() => new("x", HttpStatusCode.ServiceUnavailable, isTransient: true);

    private static NotificationHubException Throttled(TimeSpan retryAfter) => new("x", HttpStatusCode.TooManyRequests, isTransient: true, retryAfter);
}
