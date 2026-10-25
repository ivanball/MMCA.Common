using System.Globalization;
using System.Xml.Linq;
using AwesomeAssertions;
using MMCA.Common.Gcp.DataProtection;

namespace MMCA.Common.Gcp.Tests.DataProtection;

/// <summary>
/// The Cloud Storage key-ring repository against an in-memory store with real generation semantics:
/// a missing object is an empty ring, the first write may only create the object (generation 0), a
/// later write is conditioned on the generation it read, and a writer that loses the race re-reads
/// and retries, so no element is lost; the retry is bounded.
/// </summary>
public sealed class GcsXmlRepositoryTests : IDisposable
{
    private const string Bucket = "mmca-keys";
    private const string ObjectName = "adc/prod/keys.xml";

    private readonly InMemoryGcsStorageClient _storage = new();

    public void Dispose() => _storage.Dispose();

    private GcsXmlRepository CreateSut() =>
        new(_storage, new GcsObjectLocation(Bucket, ObjectName), retryDelay: TimeSpan.Zero);

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

        _storage.Uploads.Should().ContainSingle()
            .Which!.IfGenerationMatch.Should().Be(0, "generation 0 means the write may only create the object, never replace one");
        XDocument.Parse(_storage.ContentOf(Bucket, ObjectName)!).Root!.Name.LocalName.Should().Be("repository");
        IdsOf(CreateSut().GetAllElements()).Should().Equal("a");
    }

    [Fact]
    public void StoreElement_OnAnExistingObject_AppendsUnderTheGenerationItRead()
    {
        var sut = CreateSut();
        sut.StoreElement(Key("a"), "key-a");
        var firstGeneration = _storage.GetObject(Bucket, ObjectName).Generation;

        sut.StoreElement(Key("b"), "key-b");

        _storage.Uploads.Should().HaveCount(2);
        _storage.Uploads[1]!.IfGenerationMatch.Should().Be(firstGeneration);
        IdsOf(sut.GetAllElements()).Should().Equal("a", "b");
    }

    [Fact]
    public void StoreElement_WhenAConcurrentWriterWins_RetriesAndBothElementsSurvive()
    {
        var sut = CreateSut();
        var other = CreateSut();
        sut.StoreElement(Key("a"), "key-a");

        // Another replica writes between this one's read and its conditional write, so the first
        // attempt carries a stale generation and the store answers 412.
        _storage.BeforeNextUpload = () => other.StoreElement(Key("other"), "key-other");

        sut.StoreElement(Key("b"), "key-b");

        _storage.Uploads.Select(u => u!.IfGenerationMatch).Should().HaveCount(4, "the initial write, the concurrent write, the rejected attempt and the retry");
        IdsOf(CreateSut().GetAllElements()).Should().BeEquivalentTo("a", "other", "b");
    }

    [Fact]
    public void StoreElement_WhenThePreconditionNeverHolds_GivesUpAfterTheBound_NamingTheObject()
    {
        var sut = CreateSut();
        var counter = 0;
        _storage.BeforeEveryUpload = () => _storage.Put(Bucket, ObjectName, string.Create(CultureInfo.InvariantCulture, $"<repository><key id=\"w{counter++}\" /></repository>"));

        var act = () => sut.StoreElement(Key("b"), "key-b");

        act.Should().Throw<InvalidOperationException>().WithMessage($"*gs://{Bucket}/{ObjectName}*");
        _storage.Uploads.Should().HaveCount(GcsXmlRepository.MaxAttempts);
    }

    [Fact]
    public void StoreElement_DoesNotTakeOwnershipOfTheCallersElement()
    {
        var element = Key("a");

        CreateSut().StoreElement(element, "key-a");

        element.Parent.Should().BeNull("the element is copied into the document, so the caller's instance stays detached");
    }
}
