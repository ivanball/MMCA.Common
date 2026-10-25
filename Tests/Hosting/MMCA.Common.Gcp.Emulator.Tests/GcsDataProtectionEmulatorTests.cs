using System.Xml.Linq;
using AwesomeAssertions;
using Google.Apis.Upload;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace MMCA.Common.Gcp.Emulator.Tests;

/// <summary>
/// The shipped Cloud Storage key ring, registered through <c>AddGoogleCloudDataProtection</c> exactly
/// as a host would, against fake-gcs-server: two independent hosts on one object share the key ring
/// (a payload protected by one unprotects on the other), a missing object reads as an empty ring, and
/// the generation precondition is honored by a real JSON API endpoint, so a writer that loses the race
/// is rejected, re-reads and retries without dropping either key. Cloud KMS has no emulator; its
/// encryption is covered by the unit tier.
/// </summary>
public sealed class GcsDataProtectionEmulatorTests(FakeGcsServerFixture emulator) : IAsyncLifetime
{
    private readonly string _bucket = $"mmca-{Guid.NewGuid():N}";
    private StorageClient _client = null!;

    private string ObjectUri => $"gs://{_bucket}/adc/prod/keys.xml";

    public async ValueTask InitializeAsync()
    {
        _client = emulator.CreateStorageClient();

        // The adapter never creates its bucket (infrastructure provisions it), so the test does.
        await _client.CreateBucketAsync("mmca-test", _bucket, cancellationToken: TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void TwoHosts_OnOneObject_ProtectOnOne_AndUnprotectOnTheOther()
    {
        using var first = BuildHost(_client);
        using var secondClient = emulator.CreateStorageClient();
        using var second = BuildHost(secondClient);

        var protectedPayload = first.GetDataProtector("tests").Protect("hello");

        second.GetDataProtector("tests").Unprotect(protectedPayload).Should().Be(
            "hello",
            because: "both hosts read the one key ring object, so a cookie minted by one replica is readable by the next");
        var stored = XDocument.Parse(DownloadText());
        stored.Root!.Name.LocalName.Should().Be("repository");
        stored.Root.Elements("key").Should().ContainSingle("the second host found the first host's key instead of creating its own");
    }

    [Fact]
    public void GetAllElements_OnAMissingObject_IsEmpty()
    {
        using var host = BuildHost(_client);

        RepositoryOf(host).GetAllElements().Should().BeEmpty();
    }

    [Fact]
    public void StoreElement_WhenAConcurrentWriterWins_IsRetried_AndBothElementsSurvive()
    {
        using var otherClient = emulator.CreateStorageClient();
        using var otherHost = BuildHost(otherClient);
        var other = RepositoryOf(otherHost);
        using var racingInner = emulator.CreateStorageClient();
        var racing = new RacingStorageClient(racingInner);
        using var host = BuildHost(racing);
        var repository = RepositoryOf(host);
        repository.StoreElement(new XElement("key", new XAttribute("id", "a")), "key-a");

        // The other replica writes after this one has read the ring and before its conditional write
        // lands, so the emulator must reject the stale generation with 412 for the retry to happen.
        racing.BeforeNextUpload = () => other.StoreElement(new XElement("key", new XAttribute("id", "other")), "key-other");

        repository.StoreElement(new XElement("key", new XAttribute("id", "b")), "key-b");

        racing.Uploads.Should().Be(3, "the first write, the attempt the emulator rejected, and the retry");
        repository.GetAllElements().Select(e => e.Attribute("id")!.Value).Should().BeEquivalentTo("a", "other", "b");
    }

    private static IXmlRepository RepositoryOf(ServiceProvider host) =>
        host.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository!;

    private ServiceProvider BuildHost(StorageClient client)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataProtection:GcsObjectUri"] = ObjectUri,
            ["DataProtection:ApplicationName"] = "mmca-emulator-tests",
        });

        // The client a host on the emulator registers first; the key ring uses it as is.
        builder.Services.AddSingleton(client);
        builder.AddGoogleCloudDataProtection();
        return builder.Services.BuildServiceProvider();
    }

    private string DownloadText()
    {
        using var content = new MemoryStream();
        _client.DownloadObject(_bucket, "adc/prod/keys.xml", content);
        return System.Text.Encoding.UTF8.GetString(content.ToArray());
    }

    /// <summary>
    /// Forwards the key ring's three calls to a real emulator client, running a one-shot hook before
    /// the next upload is sent, which is how the test puts another writer inside the read-modify-write
    /// window without depending on timing.
    /// </summary>
    private sealed class RacingStorageClient(StorageClient inner) : StorageClient
    {
        public Action? BeforeNextUpload { get; set; }

        public int Uploads { get; private set; }

        public override StorageObject GetObject(string bucket, string objectName, GetObjectOptions? options = null) =>
            inner.GetObject(bucket, objectName, options);

        public override StorageObject DownloadObject(
            string bucket,
            string objectName,
            Stream destination,
            DownloadObjectOptions? options = null,
            IProgress<Google.Apis.Download.IDownloadProgress>? progress = null) =>
            inner.DownloadObject(bucket, objectName, destination, options, progress);

        public override StorageObject UploadObject(
            StorageObject destination,
            Stream source,
            UploadObjectOptions? options = null,
            IProgress<IUploadProgress>? progress = null)
        {
            var hook = BeforeNextUpload;
            BeforeNextUpload = null;
            hook?.Invoke();
            Uploads++;
            return inner.UploadObject(destination, source, options, progress);
        }
    }
}
