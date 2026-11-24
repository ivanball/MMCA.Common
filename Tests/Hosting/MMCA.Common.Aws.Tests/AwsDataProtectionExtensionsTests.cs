using Amazon.S3;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.Aws.DataProtection;
using Moq;

namespace MMCA.Common.Aws.Tests;

/// <summary>
/// Guards the configuration gate on <c>AddAwsDataProtection</c>: no <c>DataProtection:S3ObjectUri</c>
/// means the host keeps the in-memory default, takes no AWS dependency and leaves no tripwire receipt,
/// while a configured object swaps the key-ring repository for the S3 one. The tests assert on the
/// resolved options of a built provider over a mocked S3 client, so no test reaches AWS; the real
/// round trip is the LocalStack tier.
/// </summary>
public sealed class AwsDataProtectionExtensionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAwsDataProtection_WithoutAnObjectUri_RegistersNothing(string? objectUri)
    {
        var builder = BuilderWith(new() { ["DataProtection:S3ObjectUri"] = objectUri });

        builder.AddAwsDataProtection();

        builder.Services.Should().NotContain(
            d => d.ServiceType == typeof(IDataProtectionProvider),
            because: "an unconfigured host must not pull in DataProtection services at all, so local development and tests stay AWS-free");
        builder.Services.Should().NotContain(
            d => d.ImplementationInstance is HostIntegrationMarker,
            because: "a call that registered nothing leaves no receipt");

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository.Should().BeNull();
    }

    [Fact]
    public void AddAwsDataProtection_WithAnObjectUri_PersistsTheKeyRingToThatS3Object()
    {
        var builder = BuilderWith(new()
        {
            ["DataProtection:S3ObjectUri"] = "s3://adc-prod-keys/dataprotection/keys.xml",
            ["DataProtection:KmsKeyId"] = "alias/adc-dataprotection",
            ["DataProtection:ApplicationName"] = "mmca-data-protection-tests",
        });

        // The client a host on LocalStack, or with its own client settings, registers first. The
        // package resolves it from the container, so this one is used as is.
        builder.Services.AddSingleton(new Mock<IAmazonS3>().Object);

        builder.AddAwsDataProtection();

        using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<DataProtectionOptions>>()
            .Value.ApplicationDiscriminator
            .Should().Be(
                "mmca-data-protection-tests",
                because: "the discriminator is what keeps two applications sharing one object from reading each other's keys");

        var repository = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;
        repository.Should().BeOfType<S3XmlRepository>(because: "a configured object must replace the in-memory default key-ring repository")
            .Which.Location.Should().Be(new S3ObjectLocation("adc-prod-keys", "dataprotection/keys.xml"));

        builder.Services.Should().Contain(d =>
            d.ImplementationInstance is HostIntegrationMarker
            && ((HostIntegrationMarker)d.ImplementationInstance).Name == HostIntegrationNames.AwsDataProtection);
    }

    [Fact]
    public void AddAwsDataProtection_WithoutAnApplicationName_UsesTheHostApplicationName()
    {
        var builder = BuilderWith(new() { ["DataProtection:S3ObjectUri"] = "s3://adc-prod-keys/keys.xml" });
        builder.Services.AddSingleton(new Mock<IAmazonS3>().Object);

        builder.AddAwsDataProtection();

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<DataProtectionOptions>>()
            .Value.ApplicationDiscriminator.Should().Be(builder.Environment.ApplicationName);
    }

    [Fact]
    public void AddAwsDataProtection_WithAMalformedObjectUri_FailsNamingTheKey_AndRegistersNothing()
    {
        // The retired Parameter Store prefix shape is the likeliest mistake after the move to S3.
        var builder = BuilderWith(new() { ["DataProtection:S3ObjectUri"] = "/adc/prod/dataprotection" });

        var act = () => builder.AddAwsDataProtection();

        act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:S3ObjectUri*s3://*");
        builder.Services.Should().NotContain(d => d.ImplementationInstance is HostIntegrationMarker);
    }

    private static HostApplicationBuilder BuilderWith(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
