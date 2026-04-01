using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;

namespace MMCA.Common.Application.Tests;

/// <summary>
/// Tests for <see cref="NullNotificationRecipientProvider"/> verifying the no-op behavior.
/// </summary>
public sealed class NullNotificationRecipientProviderTests
{
    private readonly NullNotificationRecipientProvider _sut = new();

    [Fact]
    public async Task GetRecipientUserIdsAsync_ReturnsEmptyList()
    {
        IReadOnlyList<int> result = await _sut.GetRecipientUserIdsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecipientUserIdsAsync_WithCancellationToken_ReturnsEmptyList()
    {
        using var cts = new CancellationTokenSource();
        IReadOnlyList<int> result = await _sut.GetRecipientUserIdsAsync(cts.Token);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecipientUserIdsAsync_ReturnsConsistentEmptyList()
    {
        IReadOnlyList<int> result1 = await _sut.GetRecipientUserIdsAsync();
        IReadOnlyList<int> result2 = await _sut.GetRecipientUserIdsAsync();

        result1.Should().BeEmpty();
        result2.Should().BeEmpty();
    }
}
