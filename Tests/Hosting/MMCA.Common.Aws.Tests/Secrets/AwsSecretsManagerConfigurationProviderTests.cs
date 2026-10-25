using System.Globalization;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using MMCA.Common.Aws.Secrets;
using Moq;

namespace MMCA.Common.Aws.Tests.Secrets;

/// <summary>
/// The Secrets Manager configuration provider against a mocked client: which secrets it reads (only
/// those whose name starts with the prefix, compared ordinally because the service's own name filter
/// is a case-insensitive prefix match), how each name becomes a configuration key (prefix stripped,
/// <c>--</c> to <c>:</c>, the Key Vault convention), how the values are fetched (batches of at most
/// 20, the BatchGetSecretValue cap for an id list), and that a failed read is loud at startup and
/// harmless on a timed reload.
/// </summary>
public sealed class AwsSecretsManagerConfigurationProviderTests
{
    private const string Prefix = "adc/prod/";

    [Theory]
    [InlineData("adc/prod/ConnectionStrings--Default", "ConnectionStrings:Default")]
    [InlineData("adc/prod/Jwt--SigningKey", "Jwt:SigningKey")]
    [InlineData("adc/prod/Plain", "Plain")]
    [InlineData("adc/prod/A--B--C", "A:B:C")]
    public void KeyFor_StripsThePrefix_AndMapsDoubleDashToTheSeparator(string secretName, string expected) =>
        AwsSecretsManagerConfigurationProvider.KeyFor(secretName, Prefix).Should().Be(expected);

    [Theory]
    [InlineData("adc/staging/ConnectionStrings--Default")]
    [InlineData("ADC/PROD/ConnectionStrings--Default")]
    [InlineData("adc/prod/")]
    [InlineData("other")]
    public void KeyFor_OutsideThePrefix_OrThePrefixItself_IsNull(string secretName) =>
        AwsSecretsManagerConfigurationProvider.KeyFor(secretName, Prefix).Should().BeNull();

    [Fact]
    public void Load_ReadsEverySecretUnderThePrefix_AsConfigurationKeys()
    {
        var store = new FakeSecretsStore(new()
        {
            ["adc/prod/ConnectionStrings--Default"] = "Server=db;Database=adc",
            ["adc/prod/Jwt--SigningKey"] = "signing-key",
        });

        var configuration = BuildConfiguration(store);

        configuration["ConnectionStrings:Default"].Should().Be("Server=db;Database=adc");
        configuration.GetConnectionString("Default").Should().Be("Server=db;Database=adc");
        configuration["Jwt:SigningKey"].Should().Be("signing-key");
    }

    [Fact]
    public void Load_FiltersTheListingByName_AndIgnoresASecretTheServiceFilterLetThrough()
    {
        // The service's name filter is a case-insensitive prefix match, so a secret named for another
        // environment in a different case comes back from the listing and must still be dropped.
        var store = new FakeSecretsStore(new()
        {
            ["adc/prod/Jwt--SigningKey"] = "prod-key",
            ["ADC/PROD/Jwt--Issuer"] = "wrong-environment",
        });

        var configuration = BuildConfiguration(store);

        store.ListRequests.Should().NotBeEmpty();
        store.ListRequests.Should().OnlyContain(r =>
            r.Filters.Count == 1
            && r.Filters[0].Key == FilterNameStringType.Name
            && r.Filters[0].Values.SequenceEqual(new[] { Prefix }));
        configuration["Jwt:SigningKey"].Should().Be("prod-key");
        configuration["Jwt:Issuer"].Should().BeNull();
        store.BatchRequests.SelectMany(r => r.SecretIdList).Should().NotContain("ADC/PROD/Jwt--Issuer");
    }

    [Fact]
    public void Load_FetchesValuesInBatchesOfTwenty_AcrossEveryListingPage()
    {
        var secrets = Enumerable.Range(0, 45).ToDictionary(
            i => string.Create(CultureInfo.InvariantCulture, $"adc/prod/Key{i:D2}"),
            i => (string?)string.Create(CultureInfo.InvariantCulture, $"value-{i}"),
            StringComparer.Ordinal);
        var store = new FakeSecretsStore(secrets, listPageSize: 10);

        var configuration = BuildConfiguration(store);

        store.ListRequests.Should().HaveCount(5, "45 secrets at 10 a page is five listing pages");
        store.BatchRequests.Select(r => r.SecretIdList.Count).Should().Equal(20, 20, 5);
        configuration["Key00"].Should().Be("value-0");
        configuration["Key44"].Should().Be("value-44");
    }

    [Fact]
    public void Load_SkipsASecretWithoutAStringValue()
    {
        var store = new FakeSecretsStore(new()
        {
            ["adc/prod/Text"] = "text",
            ["adc/prod/Binary"] = null,
        });

        var configuration = BuildConfiguration(store);

        configuration["Text"].Should().Be("text");
        configuration.AsEnumerable().Should().NotContain(pair => pair.Key == "Binary");
    }

    [Fact]
    public void Load_WithAFailedSecretRead_FailsNamingTheSecret()
    {
        var store = new FakeSecretsStore(new() { ["adc/prod/Jwt--SigningKey"] = "key" })
        {
            FailSecret = "adc/prod/Jwt--SigningKey",
        };

        var act = () => BuildConfiguration(store);

        act.Should().Throw<InvalidOperationException>(
            because: "a secret the host cannot read must stop startup rather than leave the setting silently missing")
            .WithMessage("*adc/prod/Jwt--SigningKey*AccessDeniedException*");
    }

    [Fact]
    public async Task ReloadInterval_PicksUpAChangedValue_AndRaisesTheChangeToken()
    {
        var store = new FakeSecretsStore(new() { ["adc/prod/Jwt--SigningKey"] = "first" });
        using var provider = new AwsSecretsManagerConfigurationProvider(store.Client, Prefix, TimeSpan.FromMilliseconds(50));
        provider.Load();
        provider.TryGet("Jwt:SigningKey", out var initial).Should().BeTrue();
        initial.Should().Be("first");

        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = provider.GetReloadToken().RegisterChangeCallback(_ => reloaded.TrySetResult(), null);
        store.Set("adc/prod/Jwt--SigningKey", "second");

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        provider.TryGet("Jwt:SigningKey", out var current).Should().BeTrue();
        current.Should().Be("second");
    }

    [Fact]
    public async Task ReloadInterval_AFailedReload_KeepsTheValuesItAlreadyHas()
    {
        var store = new FakeSecretsStore(new() { ["adc/prod/Jwt--SigningKey"] = "first" });
        using var provider = new AwsSecretsManagerConfigurationProvider(store.Client, Prefix, TimeSpan.FromMilliseconds(50));
        provider.Load();

        store.FailSecret = "adc/prod/Jwt--SigningKey";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (provider.LastReloadFailure is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        provider.LastReloadFailure.Should().NotBeNull("the timed reload ran and failed");
        provider.TryGet("Jwt:SigningKey", out var current).Should().BeTrue();
        current.Should().Be("first", "a transient read failure on a reload must not blank a setting the host is already using");
    }

    private static IConfigurationRoot BuildConfiguration(FakeSecretsStore store) =>
        new ConfigurationBuilder()
            .Add(new AwsSecretsManagerConfigurationSource(() => store.Client, Prefix, reloadInterval: null))
            .Build();

    /// <summary>
    /// An in-memory stand-in for Secrets Manager behind a mocked <see cref="IAmazonSecretsManager"/>:
    /// the listing honors the name filter the way the service does (case-insensitive prefix) and pages
    /// by <c>NextToken</c>; the batch read returns the requested values, failing one on demand.
    /// </summary>
    private sealed class FakeSecretsStore
    {
        private readonly Dictionary<string, string?> _secrets;
        private readonly int _listPageSize;
        private readonly Lock _gate = new();

        public FakeSecretsStore(Dictionary<string, string?> secrets, int listPageSize = 100)
        {
            _secrets = new Dictionary<string, string?>(secrets, StringComparer.Ordinal);
            _listPageSize = listPageSize;

            var client = new Mock<IAmazonSecretsManager>(MockBehavior.Strict);
            client.Setup(c => c.ListSecretsAsync(It.IsAny<ListSecretsRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ListSecretsRequest request, CancellationToken _) => List(request));
            client.Setup(c => c.BatchGetSecretValueAsync(It.IsAny<BatchGetSecretValueRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((BatchGetSecretValueRequest request, CancellationToken _) => Batch(request));
            client.Setup(c => c.Dispose());
            Client = client.Object;
        }

        public IAmazonSecretsManager Client { get; }

        public List<ListSecretsRequest> ListRequests { get; } = [];

        public List<BatchGetSecretValueRequest> BatchRequests { get; } = [];

        public string? FailSecret { get; set; }

        public void Set(string name, string? value)
        {
            lock (_gate)
            {
                _secrets[name] = value;
            }
        }

        private ListSecretsResponse List(ListSecretsRequest request)
        {
            lock (_gate)
            {
                ListRequests.Add(request);
                var filter = request.Filters?.SingleOrDefault()?.Values?.SingleOrDefault() ?? string.Empty;
                var matching = _secrets.Keys
                    .Where(name => name.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                    .ToList();
                var start = string.IsNullOrEmpty(request.NextToken) ? 0 : int.Parse(request.NextToken, CultureInfo.InvariantCulture);
                var page = matching.Skip(start).Take(_listPageSize).ToList();
                var next = start + page.Count;

                return new ListSecretsResponse
                {
                    SecretList = [.. page.Select(name => new SecretListEntry { Name = name })],
                    NextToken = next < matching.Count ? next.ToString(CultureInfo.InvariantCulture) : null,
                };
            }
        }

        private BatchGetSecretValueResponse Batch(BatchGetSecretValueRequest request)
        {
            lock (_gate)
            {
                BatchRequests.Add(request);
                var response = new BatchGetSecretValueResponse { SecretValues = [], Errors = [] };
                foreach (var id in request.SecretIdList)
                {
                    if (string.Equals(id, FailSecret, StringComparison.Ordinal))
                    {
                        response.Errors.Add(new APIErrorType { SecretId = id, ErrorCode = "AccessDeniedException", Message = "denied" });
                        continue;
                    }

                    var value = _secrets[id];
                    response.SecretValues.Add(value is null
                        ? new SecretValueEntry { Name = id, SecretBinary = new MemoryStream([1, 2, 3]) }
                        : new SecretValueEntry { Name = id, SecretString = value });
                }

                return response;
            }
        }
    }
}
