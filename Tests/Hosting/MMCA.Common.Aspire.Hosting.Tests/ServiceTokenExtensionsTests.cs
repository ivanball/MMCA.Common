using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Grpc.Authentication;

namespace MMCA.Common.Aspire.Hosting.Tests;

/// <summary>
/// Unit tests for the service-token signing key helpers: <c>AddServiceTokenSigningKey</c> provisions
/// the local HS256 key every gRPC service-token minter and validator shares (a generated, persisted
/// secret parameter), and <c>WithServiceTokenSigningKey</c> hands it to one service under the key
/// <see cref="ServiceTokenSettings"/> binds. The default parameter name is the slot Store already
/// persists in user secrets, so adopting the helper regenerates no developer key.
/// </summary>
public sealed class ServiceTokenExtensionsTests
{
    private const string DefaultName = "grpc-service-token-signing-key";

    [Fact]
    public void AddServiceTokenSigningKey_IsSecret_WithTheDefaultName()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var signingKey = builder.AddServiceTokenSigningKey();

        ServiceTokenExtensions.DefaultSigningKeyParameterName.Should().Be(DefaultName);
        signingKey.Resource.Name.Should().Be(DefaultName);
        signingKey.Resource.Secret.Should().BeTrue();

        // DESIGN-QUESTION: rule 24.2 (persist: true) is not observable here. Aspire.Hosting 13.6.5 keeps
        // its UserSecretsParameterDefault wrapper internal, so the test cannot tell a persisted
        // parameter from an unpersisted one; only the name, the secret flag and the generator shape
        // are asserted.
        // persist: true lets Aspire wrap the generator in its (internal) user-secrets default in run
        // mode, so the default is either the generator itself or that wrapper. The generated value's
        // shape is pinned by the next test, which holds whichever of the two Aspire chose.
        signingKey.Resource.Default.Should().NotBeNull(because: "the key is generated when configuration supplies none");
        if (signingKey.Resource.Default is GenerateParameterDefault generator)
        {
            generator.MinLength.Should().Be(64);
            generator.Special.Should().BeFalse();
        }
    }

    [Fact]
    public async Task GeneratedValue_Is64Alphanumerics_DecodingTo48Bytes()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var signingKey = builder.AddServiceTokenSigningKey();

        var value = await signingKey.Resource.GetValueAsync(TestContext.Current.CancellationToken);

        value.Should().MatchRegex(
            "^[A-Za-z0-9]{64}$",
            because: "64 letters and digits, since Special = false keeps the value valid Base64");
        Convert.FromBase64String(value!).Should().HaveCount(
            48,
            because: "64 Base64 characters decode to 48 bytes, above the 32-byte floor ServiceTokenSettings enforces");
    }

    [Fact]
    public void CustomName_IsUsed()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var signingKey = builder.AddServiceTokenSigningKey("custom-signing-key");

        signingKey.Resource.Name.Should().Be("custom-signing-key");
        signingKey.Resource.Secret.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankName_Throws(string name)
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var act = () => builder.AddServiceTokenSigningKey(name);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NullArguments_Throw()
    {
        IDistributedApplicationBuilder nullBuilder = null!;
        IResourceBuilder<ProjectResource> nullService = null!;
        var builder = DistributedApplication.CreateBuilder([]);
        var signingKey = builder.AddServiceTokenSigningKey();
        var service = builder.AddResource(new ProjectResource("identity"));

        FluentActions.Invoking(() => nullBuilder.AddServiceTokenSigningKey())
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => nullService.WithServiceTokenSigningKey(signingKey))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => service.WithServiceTokenSigningKey(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task WithServiceTokenSigningKey_SetsTheKey()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var signingKey = builder.AddServiceTokenSigningKey();
        var control = builder.AddResource(new ProjectResource("control"))
            .WithEnvironment("Expected", signingKey);
        var service = builder.AddResource(new ProjectResource("identity"));

        service.WithServiceTokenSigningKey(signingKey);

        var expected = (await EnvironmentOf(builder, control.Resource))["Expected"];
        var environment = await EnvironmentOf(builder, service.Resource);

        environment.Should().ContainKey("Grpc__ServiceToken__SigningKey")
            .WhoseValue.Should().Be(expected, because: "the service receives the parameter itself, not a copy of its value");
    }

    [Fact]
    public async Task Key_DerivesFromServiceTokenSettings()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var signingKey = builder.AddServiceTokenSigningKey();
        var service = builder.AddResource(new ProjectResource("identity"));

        service.WithServiceTokenSigningKey(signingKey);

        var environment = await EnvironmentOf(builder, service.Resource);

        environment.Should().ContainKey(
            ServiceTokenSettings.SectionName.Replace(":", "__", StringComparison.Ordinal)
            + "__"
            + nameof(ServiceTokenSettings.SigningKey));
    }

    // The internal constants mirror names ServiceTokenSettings owns (design 24(a)); this keeps the
    // mirror honest.
    [Fact]
    public void MirroredConstants_MatchServiceTokenSettings()
    {
        ServiceTokenExtensions.ServiceTokenSection.Should().Be(ServiceTokenSettings.SectionName);
        ServiceTokenExtensions.SigningKeyProperty.Should().Be(nameof(ServiceTokenSettings.SigningKey));
    }

    [Fact]
    public void ReturnsTheSameBuilder()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var signingKey = builder.AddServiceTokenSigningKey();
        var service = builder.AddResource(new ProjectResource("identity"));

        service.WithServiceTokenSigningKey(signingKey).Should().BeSameAs(service);
    }

    /// <summary>
    /// Resolves a resource's environment variables in publish mode, where every reference stays a
    /// manifest expression rather than a value DCP happened to allocate.
    /// </summary>
    private static async Task<Dictionary<string, string>> EnvironmentOf(
        IDistributedApplicationBuilder builder,
        IResource resource)
    {
        await using ServiceProvider services = builder.Services.BuildServiceProvider();
        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Publish)
            {
                Services = services,
            });

        IExecutionConfigurationResult result = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext);

        result.Exception.Should().BeNull();
        return result.EnvironmentVariables.ToDictionary(StringComparer.Ordinal);
    }
}
