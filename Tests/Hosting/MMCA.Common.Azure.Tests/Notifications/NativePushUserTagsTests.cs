using AwesomeAssertions;
using MMCA.Common.Azure.Notifications;

namespace MMCA.Common.Azure.Tests.Notifications;

/// <summary>
/// Tests for <see cref="NativePushUserTags"/> (ADR-044): the 20-tag chunking rule imposed by the
/// Azure Notification Hubs tag-expression cap. The payload shapes the hub forwards are the core
/// <c>NativePushPayloads</c>, tested in MMCA.Common.Infrastructure.Tests.
/// </summary>
public sealed class NativePushUserTagsTests
{
    [Fact]
    public void BuildUserTagExpressions_JoinsTagsWithOr()
    {
        var expressions = NativePushUserTags.BuildUserTagExpressions([1, 2, 3]).ToList();

        expressions.Should().ContainSingle()
            .Which.Should().Be("user:1 || user:2 || user:3");
    }

    [Fact]
    public void BuildUserTagExpressions_ChunksAtTheHubTagCap()
    {
        var userIds = Enumerable.Range(1, NativePushUserTags.MaxTagsPerExpression * 2 + 5);

        var expressions = NativePushUserTags.BuildUserTagExpressions(userIds).ToList();

        expressions.Should().HaveCount(3);
        expressions[0].Split(" || ").Should().HaveCount(NativePushUserTags.MaxTagsPerExpression);
        expressions[2].Split(" || ").Should().HaveCount(5);
    }

    [Fact]
    public void BuildUserTagExpressions_WithNoUsers_YieldsNothing()
        => NativePushUserTags.BuildUserTagExpressions([]).Should().BeEmpty();
}
