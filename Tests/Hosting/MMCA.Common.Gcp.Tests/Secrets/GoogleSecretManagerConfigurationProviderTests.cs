using AwesomeAssertions;
using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.SecretManager.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Gcp.Secrets;

namespace MMCA.Common.Gcp.Tests.Secrets;

/// <summary>
/// The Secret Manager configuration provider against an in-memory client: which secrets it reads
/// (only those whose id starts with the prefix, compared ordinally because the service's name filter
/// is a case-insensitive substring match), how each id becomes a configuration key (prefix stripped,
/// <c>--</c> to <c>:</c>, the Key Vault convention), that a latest version which is not enabled is
/// skipped, and that a failed read is loud at startup and harmless on a timed reload.
/// </summary>
public sealed class GoogleSecretManagerConfigurationProviderTests
{
    private const string ProjectId = "mmca-prod";
    private const string Prefix = "adc-prod-";

    [Theory]
    [InlineData("adc-prod-ConnectionStrings--Default", "ConnectionStrings:Default")]
    [InlineData("adc-prod-Jwt--SigningKey", "Jwt:SigningKey")]
    [InlineData("adc-prod-Plain", "Plain")]
    [InlineData("adc-prod-A--B--C", "A:B:C")]
    public void KeyFor_StripsThePrefix_AndMapsDoubleDashToTheSeparator(string secretId, string expected) =>
        GoogleSecretManagerConfigurationProvider.KeyFor(secretId, Prefix).Should().Be(expected);

    [Theory]
    [InlineData("adc-staging-ConnectionStrings--Default")]
    [InlineData("ADC-PROD-ConnectionStrings--Default")]
    [InlineData("other-adc-prod-Jwt--SigningKey")]
    [InlineData("adc-prod-")]
    public void KeyFor_OutsideThePrefix_OrThePrefixItself_IsNull(string secretId) =>
        GoogleSecretManagerConfigurationProvider.KeyFor(secretId, Prefix).Should().BeNull();

    [Fact]
    public void Load_ReadsEverySecretUnderThePrefix_AsConfigurationKeys()
    {
        var store = new FakeSecretManager();
        store.Set("adc-prod-ConnectionStrings--Default", "Server=db;Database=adc");
        store.Set("adc-prod-Jwt--SigningKey", "signing-key");

        var configuration = BuildConfiguration(store);

        configuration["ConnectionStrings:Default"].Should().Be("Server=db;Database=adc");
        configuration.GetConnectionString("Default").Should().Be("Server=db;Database=adc");
        configuration["Jwt:SigningKey"].Should().Be("signing-key");
    }

    [Fact]
    public void Load_FiltersTheListingByName_AndIgnoresASecretOutsideThePrefix()
    {
        // The service's name filter is a case-insensitive SUBSTRING match, so a secret for another
        // environment in a different case, or one that merely contains the prefix, comes back from
        // the listing and must still be dropped.
        var store = new FakeSecretManager();
        store.Set("adc-prod-Jwt--SigningKey", "prod-key");
        store.Set("ADC-PROD-Jwt--Issuer", "wrong-environment");
        store.Set("old-adc-prod-Jwt--Audience", "contains-the-prefix");
        store.Set("adc-staging-Jwt--SigningKey", "staging-key");

        var configuration = BuildConfiguration(store);

        store.ListRequests.Should().ContainSingle()
            .Which.Should().Match<ListSecretsRequest>(r => r.Parent == "projects/mmca-prod" && r.Filter == "name:\"adc-prod-\"");
        configuration["Jwt:SigningKey"].Should().Be("prod-key");
        configuration["Jwt:Issuer"].Should().BeNull();
        configuration["Jwt:Audience"].Should().BeNull();
        store.AccessedSecrets.Should().Equal("adc-prod-Jwt--SigningKey");
    }

    [Theory]
    [InlineData(SecretVersion.Types.State.Disabled)]
    [InlineData(SecretVersion.Types.State.Destroyed)]
    public void Load_SkipsASecretWhoseLatestVersionIsNotEnabled(SecretVersion.Types.State state)
    {
        var store = new FakeSecretManager();
        store.Set("adc-prod-Text", "text");
        store.Set("adc-prod-Retired", "old", state);

        var configuration = BuildConfiguration(store);

        configuration["Text"].Should().Be("text");
        configuration.AsEnumerable().Should().NotContain(pair => pair.Key == "Retired");
        store.AccessedSecrets.Should().NotContain("adc-prod-Retired", "a version that is not enabled cannot be accessed, so it is never asked for");
    }

    [Fact]
    public void Load_SkipsASecretWithNoVersionYet()
    {
        var store = new FakeSecretManager();
        store.Set("adc-prod-Text", "text");
        store.AddEmptySecret("adc-prod-Pending");

        var configuration = BuildConfiguration(store);

        configuration["Text"].Should().Be("text");
        configuration.AsEnumerable().Should().NotContain(pair => pair.Key == "Pending");
    }

    [Fact]
    public void Load_AccessesTheConcreteLatestVersion_NotTheAlias()
    {
        // The state check and the read address the same version, so a rotation landing between the
        // two calls cannot hand back a value whose state was never checked.
        var store = new FakeSecretManager();
        store.Set("adc-prod-Jwt--SigningKey", "key");

        BuildConfiguration(store);

        store.AccessRequests.Should().ContainSingle()
            .Which.Should().Be("projects/mmca-prod/secrets/adc-prod-Jwt--SigningKey/versions/1");
    }

    [Fact]
    public void Load_WithAFailedSecretRead_FailsNamingTheSecret()
    {
        var store = new FakeSecretManager { FailSecret = "adc-prod-Jwt--SigningKey" };
        store.Set("adc-prod-Jwt--SigningKey", "key");

        var act = () => BuildConfiguration(store);

        act.Should().Throw<InvalidOperationException>(
            because: "a secret the host cannot read must stop startup rather than leave the setting silently missing")
            .WithMessage("*adc-prod-Jwt--SigningKey*PermissionDenied*");
    }

    [Fact]
    public void Load_WithAFailedListing_FailsNamingTheProject()
    {
        var store = new FakeSecretManager { FailListing = true };

        var act = () => BuildConfiguration(store);

        act.Should().Throw<InvalidOperationException>().WithMessage("*mmca-prod*PermissionDenied*");
    }

    [Fact]
    public async Task ReloadInterval_PicksUpAChangedValue_AndRaisesTheChangeToken()
    {
        var store = new FakeSecretManager();
        store.Set("adc-prod-Jwt--SigningKey", "first");
        using var provider = new GoogleSecretManagerConfigurationProvider(store, ProjectId, Prefix, TimeSpan.FromMilliseconds(50));
        provider.Load();
        provider.TryGet("Jwt:SigningKey", out var initial).Should().BeTrue();
        initial.Should().Be("first");

        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = provider.GetReloadToken().RegisterChangeCallback(_ => reloaded.TrySetResult(), null);
        store.Set("adc-prod-Jwt--SigningKey", "second");

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        provider.TryGet("Jwt:SigningKey", out var current).Should().BeTrue();
        current.Should().Be("second");
    }

    [Fact]
    public async Task ReloadInterval_AFailedReload_KeepsTheValuesItAlreadyHas()
    {
        var store = new FakeSecretManager();
        store.Set("adc-prod-Jwt--SigningKey", "first");
        using var provider = new GoogleSecretManagerConfigurationProvider(store, ProjectId, Prefix, TimeSpan.FromMilliseconds(50));
        provider.Load();

        store.FailSecret = "adc-prod-Jwt--SigningKey";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (provider.LastReloadFailure is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        provider.LastReloadFailure.Should().NotBeNull("the timed reload ran and failed");
        provider.TryGet("Jwt:SigningKey", out var current).Should().BeTrue();
        current.Should().Be("first", "a transient read failure on a reload must not blank a setting the host is already using");
    }

    private static IConfigurationRoot BuildConfiguration(FakeSecretManager store) =>
        new ConfigurationBuilder()
            .Add(new GoogleSecretManagerConfigurationSource(() => store, ProjectId, Prefix, reloadInterval: null))
            .Build();

    /// <summary>
    /// An in-memory stand-in for Secret Manager: the listing honors the name filter the way the
    /// service does (case-insensitive substring of the resource name), <c>latest</c> resolves to the
    /// newest version whatever its state, and a version that is not enabled refuses access.
    /// </summary>
    private sealed class FakeSecretManager : SecretManagerServiceClient
    {
        private readonly Dictionary<string, List<(string Value, SecretVersion.Types.State State)>> _secrets = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public List<ListSecretsRequest> ListRequests { get; } = [];

        public List<string> AccessRequests { get; } = [];

        public List<string> AccessedSecrets { get; } = [];

        public string? FailSecret { get; set; }

        public bool FailListing { get; set; }

        public void Set(string secretId, string value, SecretVersion.Types.State state = SecretVersion.Types.State.Enabled)
        {
            lock (_gate)
            {
                if (!_secrets.TryGetValue(secretId, out var versions))
                {
                    versions = [];
                    _secrets[secretId] = versions;
                }

                versions.Add((value, state));
            }
        }

        public void AddEmptySecret(string secretId)
        {
            lock (_gate)
            {
                _secrets[secretId] = [];
            }
        }

        public override PagedEnumerable<ListSecretsResponse, Secret> ListSecrets(ListSecretsRequest request, CallSettings? callSettings = null)
        {
            lock (_gate)
            {
                ListRequests.Add(request);
                if (FailListing)
                {
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "denied"));
                }

                var needle = request.Filter.StartsWith("name:\"", StringComparison.Ordinal)
                    ? request.Filter["name:\"".Length..^1]
                    : string.Empty;
                var project = ProjectNameOf(request.Parent);
                return new StaticPagedEnumerable(
                [
                    .. _secrets.Keys
                        .Select(id => new Secret { SecretName = new SecretName(project, id) })
                        .Where(secret => secret.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)),
                ]);
            }
        }

        public override SecretVersion GetSecretVersion(GetSecretVersionRequest request, CallSettings? callSettings = null)
        {
            lock (_gate)
            {
                var name = SecretVersionName.Parse(request.Name);
                FailIfAsked(name.SecretId);
                if (!_secrets.TryGetValue(name.SecretId, out var versions) || versions.Count == 0)
                {
                    throw new RpcException(new Status(StatusCode.NotFound, "no version"));
                }

                var number = name.SecretVersionId == "latest" ? versions.Count : int.Parse(name.SecretVersionId, System.Globalization.CultureInfo.InvariantCulture);
                return new SecretVersion
                {
                    SecretVersionName = new SecretVersionName(name.ProjectId, name.SecretId, number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    State = versions[number - 1].State,
                };
            }
        }

        public override AccessSecretVersionResponse AccessSecretVersion(AccessSecretVersionRequest request, CallSettings? callSettings = null)
        {
            lock (_gate)
            {
                AccessRequests.Add(request.Name);
                var name = SecretVersionName.Parse(request.Name);
                FailIfAsked(name.SecretId);
                AccessedSecrets.Add(name.SecretId);
                var versions = _secrets[name.SecretId];
                var number = name.SecretVersionId == "latest" ? versions.Count : int.Parse(name.SecretVersionId, System.Globalization.CultureInfo.InvariantCulture);
                var (value, state) = versions[number - 1];
                if (state != SecretVersion.Types.State.Enabled)
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, $"version is {state}"));
                }

                return new AccessSecretVersionResponse
                {
                    Name = request.Name,
                    Payload = new SecretPayload { Data = ByteString.CopyFromUtf8(value) },
                };
            }
        }

        private static string ProjectNameOf(string parent) => parent["projects/".Length..];

        private void FailIfAsked(string secretId)
        {
            if (string.Equals(secretId, FailSecret, StringComparison.Ordinal))
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "denied"));
            }
        }
    }

    /// <summary>A one-page listing: the provider only enumerates the resources, the paging is Gax's.</summary>
    private sealed class StaticPagedEnumerable(List<Secret> secrets) : PagedEnumerable<ListSecretsResponse, Secret>
    {
        public override IEnumerator<Secret> GetEnumerator() => secrets.GetEnumerator();
    }
}
