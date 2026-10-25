using AwesomeAssertions;
using MMCA.Common.Gcp.DataProtection;

namespace MMCA.Common.Gcp.Tests.DataProtection;

/// <summary>
/// The <c>gs://bucket/object</c> form of <c>DataProtection:GcsObjectUri</c>: a well-formed value
/// splits into bucket and object name, and anything else fails naming the key, so a typo surfaces at
/// startup instead of as a key ring written somewhere unexpected.
/// </summary>
public sealed class GcsObjectLocationTests
{
    private const string Key = "DataProtection:GcsObjectUri";

    [Theory]
    [InlineData("gs://mmca-keys/keys.xml", "mmca-keys", "keys.xml")]
    [InlineData("gs://mmca-keys/adc/prod/keys.xml", "mmca-keys", "adc/prod/keys.xml")]
    [InlineData("GS://mmca.keys_eu/keys.xml", "mmca.keys_eu", "keys.xml")]
    public void Parse_SplitsBucketAndObject(string value, string bucket, string objectName)
    {
        var location = GcsObjectLocation.Parse(value, Key);

        location.Bucket.Should().Be(bucket);
        location.ObjectName.Should().Be(objectName);
        location.ToString().Should().Be($"gs://{bucket}/{objectName}");
    }

    [Theory]
    [InlineData("https://storage.googleapis.com/mmca-keys/keys.xml")]
    [InlineData("gs://mmca-keys")]
    [InlineData("gs://mmca-keys/")]
    [InlineData("gs:///keys.xml")]
    [InlineData("gs://mmca-keys/dir/")]
    [InlineData("mmca-keys/keys.xml")]
    public void Parse_OfAMalformedValue_ThrowsNamingTheKey(string value)
    {
        var act = () => GcsObjectLocation.Parse(value, Key);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{Key}*gs://*");
    }
}
