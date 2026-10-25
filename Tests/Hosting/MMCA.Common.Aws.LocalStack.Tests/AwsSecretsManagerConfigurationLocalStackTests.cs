using System.Globalization;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Aws.LocalStack.Tests;

/// <summary>
/// The shipped Secrets Manager configuration source, added through
/// <c>AddAwsSecretsManagerConfiguration</c> exactly as a host would, against LocalStack's Secrets
/// Manager API: every secret under the prefix arrives as a configuration key (more than one batch of
/// 20, so the chunking meets the real service limit), a secret outside the prefix stays out, and a
/// reload reads the value a rotation wrote.
/// </summary>
public sealed class AwsSecretsManagerConfigurationLocalStackTests(LocalStackFixture localStack) : IAsyncLifetime
{
    private readonly string _prefix = $"mmca-{Guid.NewGuid():N}/prod/";
    private readonly List<string> _created = [];
    private AmazonSecretsManagerClient _secrets = null!;

    public ValueTask InitializeAsync()
    {
        _secrets = localStack.CreateSecretsManagerClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _created)
        {
            await _secrets.DeleteSecretAsync(new DeleteSecretRequest { SecretId = name, ForceDeleteWithoutRecovery = true });
        }

        _secrets.Dispose();
    }

    [Fact]
    public async Task SecretsUnderThePrefix_LoadIntoConfiguration_AndOthersStayOut()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAsync($"{_prefix}ConnectionStrings--Default", "Server=db;Database=adc", ct);
        await CreateAsync($"{_prefix}Jwt--SigningKey", "signing-key", ct);
        for (var i = 0; i < 23; i++)
        {
            await CreateAsync(
                string.Create(CultureInfo.InvariantCulture, $"{_prefix}Bulk--Key{i:D2}"),
                string.Create(CultureInfo.InvariantCulture, $"value-{i}"),
                ct);
        }

        var otherEnvironment = _prefix.Replace("/prod/", "/staging/", StringComparison.Ordinal);
        await CreateAsync($"{otherEnvironment}Jwt--Issuer", "wrong-environment", ct);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(localStack.SecretsManagerSettings(_prefix));

        builder.AddAwsSecretsManagerConfiguration();

        builder.Configuration.GetConnectionString("Default").Should().Be("Server=db;Database=adc");
        builder.Configuration["Jwt:SigningKey"].Should().Be("signing-key");
        builder.Configuration.GetSection("Bulk").GetChildren().Should().HaveCount(23, "25 secrets under the prefix is two batches");
        builder.Configuration["Bulk:Key22"].Should().Be("value-22");
        builder.Configuration["Jwt:Issuer"].Should().BeNull("a secret outside the prefix is not this host's");
    }

    [Fact]
    public async Task Reload_PicksUpARotatedValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"{_prefix}Jwt--SigningKey";
        await CreateAsync(name, "before-rotation", ct);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(localStack.SecretsManagerSettings(_prefix));
        builder.AddAwsSecretsManagerConfiguration();
        builder.Configuration["Jwt:SigningKey"].Should().Be("before-rotation");

        await _secrets.PutSecretValueAsync(new PutSecretValueRequest { SecretId = name, SecretString = "after-rotation" }, ct);
        ((IConfigurationRoot)builder.Configuration).Reload();

        builder.Configuration["Jwt:SigningKey"].Should().Be("after-rotation", "a reload reads the AWSCURRENT version a rotation wrote");
    }

    private async Task CreateAsync(string name, string value, CancellationToken ct)
    {
        await _secrets.CreateSecretAsync(new CreateSecretRequest { Name = name, SecretString = value }, ct);
        _created.Add(name);
    }
}
