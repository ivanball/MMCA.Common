using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using MMCA.Common.Grpc.Authentication;
using Xunit;

namespace MMCA.Common.Grpc.Tests.Authentication;

/// <summary>
/// Pins the defaults of <see cref="ServiceTokenSettings"/> and its <see cref="IValidatableObject"/>
/// rules: one <see cref="ValidationResult"/> per problem, so a misconfigured host fails on start with
/// a message naming the exact defect rather than minting tokens the receiver cannot validate.
/// </summary>
public sealed class ServiceTokenSettingsTests
{
    [Fact]
    public void SectionName_IsGrpcServiceToken() =>
        ServiceTokenSettings.SectionName.Should().Be("Grpc:ServiceToken");

    [Fact]
    public void Defaults_AreTheDocumentedIssuerAudienceAndLifetime()
    {
        var settings = new ServiceTokenSettings
        {
            SigningKey = ServiceTokenTestData.NewBase64Key(),
            ServiceName = ServiceTokenTestData.ServiceName,
        };

        settings.Issuer.Should().Be("mmca-service");
        settings.Audience.Should().Be("internal-grpc");
        settings.LifetimeMinutes.Should().Be(5);
    }

    [Fact]
    public void Validate_WithAValidConfiguration_YieldsNoResults()
    {
        var settings = ServiceTokenTestData.ValidSettings(ServiceTokenTestData.NewBase64Key());

        Validate(settings).Should().BeEmpty();
    }

    [Fact]
    public void Validate_WithAKeyLongerThan32Bytes_YieldsNoResults()
    {
        var settings = ServiceTokenTestData.ValidSettings(ServiceTokenTestData.NewBase64Key(byteCount: 64));

        Validate(settings).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    public void Validate_WithALifetimeAtTheRangeBoundary_YieldsNoResults(int lifetimeMinutes)
    {
        var settings = ServiceTokenTestData.ValidSettings(ServiceTokenTestData.NewBase64Key(), lifetimeMinutes);

        Validate(settings).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithAnEmptySigningKey_YieldsOneResult(string signingKey)
    {
        var settings = new ServiceTokenSettings { SigningKey = signingKey, ServiceName = ServiceTokenTestData.ServiceName };

        Validate(settings).Should().ContainSingle("an empty key is one problem, not also a Base64 and a length problem");
    }

    [Fact]
    public void Validate_WithANonBase64SigningKey_YieldsOneResult()
    {
        var settings = new ServiceTokenSettings
        {
            SigningKey = "this is not base64 at all!!",
            ServiceName = ServiceTokenTestData.ServiceName,
        };

        Validate(settings).Should().ContainSingle("a key that does not decode has no length to check");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    public void Validate_WithASigningKeyUnder32Bytes_YieldsOneResult(int byteCount)
    {
        var settings = ServiceTokenTestData.ValidSettings(ServiceTokenTestData.NewBase64Key(byteCount));

        Validate(settings).Should().ContainSingle("HS256 needs at least a 256-bit key");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithAnEmptyServiceName_YieldsOneResult(string serviceName)
    {
        var settings = new ServiceTokenSettings { SigningKey = ServiceTokenTestData.NewBase64Key(), ServiceName = serviceName };

        Validate(settings).Should().ContainSingle("the service name becomes the sub and svc claims");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    public void Validate_WithALifetimeOutOfRange_YieldsOneResult(int lifetimeMinutes)
    {
        var settings = ServiceTokenTestData.ValidSettings(ServiceTokenTestData.NewBase64Key(), lifetimeMinutes);

        Validate(settings).Should().ContainSingle("the lifetime must be between 1 and 60 minutes");
    }

    [Fact]
    public void Validate_WithSeveralProblems_YieldsOneResultPerProblem()
    {
        var settings = new ServiceTokenSettings { SigningKey = string.Empty, ServiceName = string.Empty, LifetimeMinutes = 0 };

        Validate(settings).Should().HaveCount(3);
    }

    private static List<ValidationResult> Validate(ServiceTokenSettings settings) =>
        [.. settings.Validate(new ValidationContext(settings))];
}
