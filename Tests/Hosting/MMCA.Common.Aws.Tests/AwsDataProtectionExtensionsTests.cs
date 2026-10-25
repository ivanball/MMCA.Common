using Amazon.SimpleSystemsManagement;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Aspire.Integrations;
using Moq;

namespace MMCA.Common.Aws.Tests;

/// <summary>
/// Guards the configuration gate on <c>AddAwsDataProtection</c>: no
/// <c>DataProtection:SsmParameterPrefix</c> means the host keeps the in-memory default, takes no AWS
/// dependency and leaves no tripwire receipt, while a configured prefix swaps the key-ring repository
/// for the Systems Manager one. The tests assert on the resolved options of a built provider over a
/// mocked Systems Manager client, so no test reaches AWS; the real round trip is the LocalStack tier.
/// </summary>
public sealed class AwsDataProtectionExtensionsTests
{
    private const string SsmRepositoryAssemblyName = "Amazon.AspNetCore.DataProtection.SSM";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAwsDataProtection_WithoutAParameterPrefix_RegistersNothing(string? prefix)
    {
        var builder = BuilderWith(new() { ["DataProtection:SsmParameterPrefix"] = prefix });

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
    public void AddAwsDataProtection_WithAParameterPrefix_PersistsTheKeyRingToSystemsManager()
    {
        var builder = BuilderWith(new()
        {
            ["DataProtection:SsmParameterPrefix"] = "/adc/prod/dataprotection",
            ["DataProtection:KmsKeyId"] = "alias/adc-dataprotection",
            ["DataProtection:ApplicationName"] = "mmca-data-protection-tests",
        });

        // The client a host on LocalStack, or with its own client settings, registers first. The
        // package resolves it from the container (TryAdd), so this one is used as is.
        builder.Services.AddSingleton(new Mock<IAmazonSimpleSystemsManagement>().Object);

        builder.AddAwsDataProtection();

        using var provider = builder.Services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<DataProtectionOptions>>()
            .Value.ApplicationDiscriminator
            .Should().Be(
                "mmca-data-protection-tests",
                because: "the discriminator is what keeps two applications sharing one prefix from reading each other's keys");

        var repository = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository;
        repository.Should().NotBeNull(because: "a configured prefix must replace the in-memory default key-ring repository");
        repository!.GetType().Assembly.GetName().Name.Should().Be(SsmRepositoryAssemblyName);

        builder.Services.Should().Contain(d =>
            d.ImplementationInstance is HostIntegrationMarker
            && ((HostIntegrationMarker)d.ImplementationInstance).Name == HostIntegrationNames.AwsDataProtection);
    }

    [Fact]
    public void AddAwsDataProtection_WithoutAnApplicationName_UsesTheHostApplicationName()
    {
        var builder = BuilderWith(new() { ["DataProtection:SsmParameterPrefix"] = "/adc/prod/dataprotection" });
        builder.Services.AddSingleton(new Mock<IAmazonSimpleSystemsManagement>().Object);

        builder.AddAwsDataProtection();

        using var provider = builder.Services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<DataProtectionOptions>>()
            .Value.ApplicationDiscriminator.Should().Be(builder.Environment.ApplicationName);
    }

    private static HostApplicationBuilder BuilderWith(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
