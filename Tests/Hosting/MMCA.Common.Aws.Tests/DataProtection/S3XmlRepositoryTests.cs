using System.Globalization;
using System.Xml.Linq;
using Amazon.S3;
using AwesomeAssertions;
using MMCA.Common.Aws.DataProtection;

namespace MMCA.Common.Aws.Tests.DataProtection;

/// <summary>
/// The S3 key-ring repository against an in-memory store with real conditional-write semantics: a
/// missing object is an empty ring, the first write may only create the object (If-None-Match), a
/// later write is conditioned on the ETag it read (If-Match), and a writer that loses the race
/// re-reads and retries, so no element is lost; the retry is bounded. A configured KMS key rides on
/// every write as SSE-KMS.
/// </summary>
public sealed class S3XmlRepositoryTests
{
    private const string Bucket = "mmca-keys";
    private const string ObjectKey = "adc/prod/keys.xml";

    private readonly InMemoryS3 _storage = new();

    private S3XmlRepository CreateSut(string? kmsKeyId = null) =>
        new(_storage.Mock.Object, new S3ObjectLocation(Bucket, ObjectKey), kmsKeyId, retryDelay: TimeSpan.Zero);

    private static XElement Key(string id) => new("key", new XAttribute("id", id));

    private static IEnumerable<string?> IdsOf(IEnumerable<XElement> elements) =>
        elements.Select(element => element.Attribute("id")?.Value);

    [Fact]
    public void GetAllElements_OnAMissingObject_IsEmpty() =>
        CreateSut().GetAllElements().Should().BeEmpty("a key ring nobody has written yet is an empty ring, not an error");

    [Fact]
    public void StoreElement_OnAMissingObject_CreatesItWithTheMustNotExistPrecondition()
    {
        CreateSut().StoreElement(Key("a"), "key-a");

        var put = _storage.Puts.Should().ContainSingle().Which;
        put.IfNoneMatch.Should().Be("*", "If-None-Match: * means the write may only create the object, never replace one");
        put.IfMatch.Should().BeNull();
        put.ContentType.Should().Be("application/xml");
        XDocument.Parse(_storage.ContentOf(Bucket, ObjectKey)!).Root!.Name.LocalName.Should().Be("repository");
        IdsOf(CreateSut().GetAllElements()).Should().Equal("a");
    }

    [Fact]
    public void StoreElement_OnAnExistingObject_AppendsUnderTheETagItRead()
    {
        var sut = CreateSut();
        sut.StoreElement(Key("a"), "key-a");
        var firstETag = _storage.ETagOf(Bucket, ObjectKey);

        sut.StoreElement(Key("b"), "key-b");

        _storage.Puts.Should().HaveCount(2);
        _storage.Puts[1].IfMatch.Should().Be(firstETag);
        _storage.Puts[1].IfNoneMatch.Should().BeNull();
        IdsOf(sut.GetAllElements()).Should().Equal("a", "b");
    }

    [Fact]
    public void StoreElement_WhenAConcurrentWriterWins_RetriesAndBothElementsSurvive()
    {
        var sut = CreateSut();
        var other = CreateSut();
        sut.StoreElement(Key("a"), "key-a");

        // Another replica writes between this one's read and its conditional write, so the first
        // attempt carries a stale ETag and the store answers 412.
        _storage.BeforeNextPut = () => other.StoreElement(Key("other"), "key-other");

        sut.StoreElement(Key("b"), "key-b");

        _storage.Puts.Should().HaveCount(4, "the initial write, the concurrent write, the rejected attempt and the retry");
        IdsOf(CreateSut().GetAllElements()).Should().BeEquivalentTo("a", "other", "b");
    }

    [Fact]
    public void StoreElement_WhenThePreconditionNeverHolds_GivesUpAfterTheBound_NamingTheObject()
    {
        var sut = CreateSut();
        var counter = 0;
        _storage.BeforeEveryPut = () => _storage.Write(Bucket, ObjectKey, string.Create(CultureInfo.InvariantCulture, $"<repository><key id=\"w{counter++}\" /></repository>"));

        var act = () => sut.StoreElement(Key("b"), "key-b");

        act.Should().Throw<InvalidOperationException>().WithMessage($"*s3://{Bucket}/{ObjectKey}*");
        _storage.Puts.Should().HaveCount(S3XmlRepository.MaxAttempts);
    }

    [Fact]
    public void StoreElement_DoesNotTakeOwnershipOfTheCallersElement()
    {
        var element = Key("a");

        CreateSut().StoreElement(element, "key-a");

        element.Parent.Should().BeNull("the element is copied into the document, so the caller's instance stays detached");
    }

    [Fact]
    public void StoreElement_WithAKmsKey_AsksForSseKmsUnderIt()
    {
        CreateSut(kmsKeyId: "alias/adc-dataprotection").StoreElement(Key("a"), "key-a");

        var put = _storage.Puts.Should().ContainSingle().Which;
        put.ServerSideEncryptionMethod.Should().Be(ServerSideEncryptionMethod.AWSKMS);
        put.ServerSideEncryptionKeyManagementServiceKeyId.Should().Be("alias/adc-dataprotection");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void StoreElement_WithoutAKmsKey_LeavesEncryptionToTheBucketDefault(string? kmsKeyId)
    {
        CreateSut(kmsKeyId).StoreElement(Key("a"), "key-a");

        var put = _storage.Puts.Should().ContainSingle().Which;
        put.ServerSideEncryptionKeyManagementServiceKeyId.Should().BeNullOrEmpty();
        put.ServerSideEncryptionMethod.Should().NotBe(ServerSideEncryptionMethod.AWSKMS);
    }
}
