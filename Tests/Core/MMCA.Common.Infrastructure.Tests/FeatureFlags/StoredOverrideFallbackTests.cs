using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Infrastructure.FeatureFlags;
using MMCA.Common.Infrastructure.Tests.TestDoubles;
using MMCA.Common.Shared.FeatureFlags;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.FeatureFlags;

/// <summary>
/// The stored-override layer must never be more fragile than the configuration it overrides: when the
/// override snapshot cannot be loaded (a transient database error, a migration not yet applied), a flag
/// answers with its configured definition and the failure is logged as a Warning, instead of every
/// feature-gated command and query throwing.
/// </summary>
public sealed class StoredOverrideFallbackTests
{
    private readonly Mock<IFeatureDefinitionProvider> _configured = new();
    private readonly Mock<IFeatureFlagOverrideSnapshot> _snapshot = new();
    private readonly RecordingLogger<StoredOverrideFeatureDefinitionProvider> _logger = new();

    public StoredOverrideFallbackTests()
    {
        _configured
            .Setup(p => p.GetFeatureDefinitionAsync("Conference.Sessions"))
            .ReturnsAsync(new FeatureDefinition { Name = "Conference.Sessions" });
        _configured
            .Setup(p => p.GetAllFeatureDefinitionsAsync())
            .Returns(Definitions(new FeatureDefinition { Name = "Conference.Sessions" }));
        _snapshot
            .Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Invalid object name 'FeatureFlagOverrides'."));
    }

    private StoredOverrideFeatureDefinitionProvider Provider => new(_configured.Object, _snapshot.Object, _logger);

    [Fact]
    public async Task GetFeatureDefinition_WhenTheSnapshotCannotLoad_AnswersWithTheConfiguredDefinition()
    {
        var definition = await Provider.GetFeatureDefinitionAsync("Conference.Sessions");

        definition.Name.Should().Be("Conference.Sessions");
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task GetAllFeatureDefinitions_WhenTheSnapshotCannotLoad_AnswersWithTheConfiguredDefinitions()
    {
        var names = new List<string>();
        await foreach (var definition in Provider.GetAllFeatureDefinitionsAsync().WithCancellation(TestContext.Current.CancellationToken))
        {
            names.Add(definition.Name);
        }

        names.Should().Equal("Conference.Sessions");
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task GetFeatureDefinition_WhenCancelled_DoesNotSwallowTheCancellation()
    {
        _snapshot
            .Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => Provider.GetFeatureDefinitionAsync("Conference.Sessions");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetFeatureDefinition_WithAStoredOverride_StillAnswersWithTheOverride()
    {
        _snapshot
            .Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FeatureFlagOverrideResponse("Conference.Sessions", FeatureFlagOverrideMode.Off, null, [], [], "incident", null, null)]);

        var definition = await Provider.GetFeatureDefinitionAsync("Conference.Sessions");

        definition.EnabledFor.Should().BeNullOrEmpty("an Off override enables the flag for no one");
        _logger.Entries.Should().BeEmpty();
    }

    private static async IAsyncEnumerable<FeatureDefinition> Definitions(params FeatureDefinition[] definitions)
    {
        foreach (var definition in definitions)
        {
            yield return definition;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
