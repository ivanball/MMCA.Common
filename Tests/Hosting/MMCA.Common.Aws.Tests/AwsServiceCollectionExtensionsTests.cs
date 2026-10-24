using Amazon.Runtime;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces.Infrastructure.Storage;
using MMCA.Common.Aws.Storage;
using MMCA.Common.Infrastructure;

namespace MMCA.Common.Aws.Tests;

/// <summary>
/// The S3 storage registration: it swaps the inert core default only when the section names a
/// bucket, binds every S3 key, and once it has, the core startup tripwire is satisfied.
/// </summary>
public sealed class AwsServiceCollectionExtensionsTests
{
    private static IConfiguration ConfigurationWith(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static Type? ImplementationOf<TService>(IServiceCollection services) =>
        services.LastOrDefault(d => d.ServiceType == typeof(TService))?.ImplementationType;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddS3FileStorage_WithoutABucket_IsANoOp(string? bucket)
    {
        var services = new ServiceCollection();

        services.AddS3FileStorage(ConfigurationWith(new()
        {
            ["FileStorage:Bucket"] = bucket,
            ["FileStorage:Region"] = "us-east-1",
        }));

        ImplementationOf<IFileStorageService>(services).Should().BeNull();
        services.Should().NotContain(d => d.ServiceType == typeof(IAmazonS3));
    }

    [Fact]
    public void AddS3FileStorage_WithABucket_RegistersTheS3Service()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddS3FileStorage(ConfigurationWith(new()
        {
            ["FileStorage:Bucket"] = "avatars",
            ["FileStorage:Region"] = "eu-west-1",
        }));

        ImplementationOf<IFileStorageService>(services).Should().Be<S3FileStorageService>();
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAmazonS3>().Config.RegionEndpoint.SystemName.Should().Be("eu-west-1");
        provider.GetRequiredService<IFileStorageService>().Should().BeOfType<S3FileStorageService>()
            .Which.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void AddS3FileStorage_BindsEverySetting()
    {
        var services = new ServiceCollection();

        services.AddS3FileStorage(ConfigurationWith(new()
        {
            ["FileStorage:Bucket"] = "documents",
            ["FileStorage:Region"] = "auto",
            ["FileStorage:ServiceUrl"] = "https://acct.r2.cloudflarestorage.com",
            ["FileStorage:ForcePathStyle"] = "true",
            ["FileStorage:PublicBaseUrl"] = "https://cdn.example.com/",
            ["FileStorage:AccessKeyId"] = "AKIDEXAMPLE",
            ["FileStorage:SecretAccessKey"] = "secret",
            ["FileStorage:RequestChecksumCalculation"] = "WHEN_SUPPORTED",
            ["FileStorage:ResponseChecksumValidation"] = "WHEN_SUPPORTED",
        }));

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<S3FileStorageSettings>>().Value;

        settings.Bucket.Should().Be("documents");
        settings.Region.Should().Be("auto");
        settings.ServiceUrl.Should().Be(new Uri("https://acct.r2.cloudflarestorage.com"));
        settings.ForcePathStyle.Should().BeTrue();
        settings.PublicBaseUrl.Should().Be(new Uri("https://cdn.example.com/"));
        settings.AccessKeyId.Should().Be("AKIDEXAMPLE");
        settings.SecretAccessKey.Should().Be("secret");
        settings.RequestChecksumCalculation.Should().Be(RequestChecksumCalculation.WHEN_SUPPORTED);
        settings.ResponseChecksumValidation.Should().Be(ResponseChecksumValidation.WHEN_SUPPORTED);
    }

    [Fact]
    public void AddS3FileStorage_WithoutChecksumKeys_DefaultsBothToWhenRequired()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddS3FileStorage(ConfigurationWith(new()
        {
            ["FileStorage:Bucket"] = "avatars",
            ["FileStorage:ServiceUrl"] = "http://localhost:9000",
        }));

        using var provider = services.BuildServiceProvider();
        var settings = provider.GetRequiredService<IOptions<S3FileStorageSettings>>().Value;
        settings.RequestChecksumCalculation.Should().Be(RequestChecksumCalculation.WHEN_REQUIRED);
        settings.ResponseChecksumValidation.Should().Be(ResponseChecksumValidation.WHEN_REQUIRED);

        var client = provider.GetRequiredService<IAmazonS3>();
        client.Config.RequestChecksumCalculation.Should().Be(RequestChecksumCalculation.WHEN_REQUIRED);
        client.Config.ResponseChecksumValidation.Should().Be(ResponseChecksumValidation.WHEN_REQUIRED);
    }

    [Theory]
    [InlineData("AKIDEXAMPLE", null)]
    [InlineData(null, "secret")]
    public void AddS3FileStorage_WithHalfAKeyPair_FailsAtRegistration(string? accessKeyId, string? secret)
    {
        var services = new ServiceCollection();

        var act = () => services.AddS3FileStorage(ConfigurationWith(new()
        {
            ["FileStorage:Bucket"] = "avatars",
            ["FileStorage:Region"] = "us-east-1",
            ["FileStorage:AccessKeyId"] = accessKeyId,
            ["FileStorage:SecretAccessKey"] = secret,
        }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*FileStorage:AccessKeyId and FileStorage:SecretAccessKey must be set together*");
    }

    [Fact]
    public void AddS3FileStorage_RejectsANullConfiguration()
    {
        var act = () => new ServiceCollection().AddS3FileStorage(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CoreTripwire_IsSatisfied_OnceTheS3AdapterIsRegistered()
    {
        // The same configuration that fails a host without the adapter call validates cleanly with it.
        var configuration = ConfigurationWith(new()
        {
            ["ConnectionStrings:SqliteConnectionString"] = "Data Source=:memory:",
            ["FileStorage:Bucket"] = "avatars",
            ["FileStorage:Region"] = "us-east-1",
        });

        using var withoutAdapter = BuildProvider(configuration, registerAdapter: false);
        using var withAdapter = BuildProvider(configuration, registerAdapter: true);

        var failing = () => withoutAdapter.GetRequiredService<IStartupValidator>().Validate();
        var passing = () => withAdapter.GetRequiredService<IStartupValidator>().Validate();

        failing.Should().Throw<OptionsValidationException>().WithMessage("*AddS3FileStorage*");
        passing.Should().NotThrow();
    }

    private static ServiceProvider BuildProvider(IConfiguration configuration, bool registerAdapter)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(configuration);
        services.AddInfrastructure(configuration);
        if (registerAdapter)
        {
            services.AddS3FileStorage(configuration);
        }

        return services.BuildServiceProvider();
    }
}
