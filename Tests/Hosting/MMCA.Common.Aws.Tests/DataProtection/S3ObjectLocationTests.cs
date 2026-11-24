using AwesomeAssertions;
using MMCA.Common.Aws.DataProtection;

namespace MMCA.Common.Aws.Tests.DataProtection;

/// <summary>
/// The <c>s3://bucket/key</c> form of <c>DataProtection:S3ObjectUri</c>: a well-formed value splits
/// into bucket and key, and anything else fails naming the configuration key, so a typo surfaces at
/// startup instead of as a key ring written somewhere unexpected.
/// </summary>
public sealed class S3ObjectLocationTests
{
    private const string ConfigurationKey = "DataProtection:S3ObjectUri";

    [Theory]
    [InlineData("s3://mmca-keys/keys.xml", "mmca-keys", "keys.xml")]
    [InlineData("s3://mmca-keys/adc/prod/keys.xml", "mmca-keys", "adc/prod/keys.xml")]
    [InlineData("S3://mmca.keys-eu/keys.xml", "mmca.keys-eu", "keys.xml")]
    public void Parse_SplitsBucketAndKey(string value, string bucket, string key)
    {
        var location = S3ObjectLocation.Parse(value, ConfigurationKey);

        location.Bucket.Should().Be(bucket);
        location.Key.Should().Be(key);
        location.ToString().Should().Be($"s3://{bucket}/{key}");
    }

    [Theory]
    [InlineData("https://mmca-keys.s3.amazonaws.com/keys.xml")]
    [InlineData("s3://mmca-keys")]
    [InlineData("s3://mmca-keys/")]
    [InlineData("s3:///keys.xml")]
    [InlineData("s3://mmca-keys/dir/")]
    [InlineData("/adc/prod/dataprotection")]
    public void Parse_OfAMalformedValue_ThrowsNamingTheKey(string value)
    {
        var act = () => S3ObjectLocation.Parse(value, ConfigurationKey);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{ConfigurationKey}*s3://*");
    }
}
