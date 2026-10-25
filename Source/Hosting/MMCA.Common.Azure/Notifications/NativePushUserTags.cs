namespace MMCA.Common.Azure.Notifications;

/// <summary>
/// Builds the user tag expressions for Azure Notification Hubs sends (ADR-044). Kept as a pure
/// helper so the 20-tag chunking rule is unit-testable without a hub. The platform payloads
/// themselves come from the core <c>NativePushPayloads</c>, shared with the other push adapters.
/// </summary>
internal static class NativePushUserTags
{
    /// <summary>Azure Notification Hubs caps tag expressions at 20 tags; larger audiences are chunked.</summary>
    internal const int MaxTagsPerExpression = 20;

    /// <summary>
    /// Produces one <c>user:a || user:b || ...</c> OR-expression per chunk of
    /// <see cref="MaxTagsPerExpression"/> users.
    /// </summary>
    internal static IEnumerable<string> BuildUserTagExpressions(IEnumerable<UserIdentifierType> userIds) =>
        userIds
            .Select(UserTag)
            .Chunk(MaxTagsPerExpression)
            .Select(chunk => string.Join(" || ", chunk));

    /// <summary>The prefix every owner tag starts with.</summary>
    internal const string UserTagPrefix = "user:";

    /// <summary>The tag stamped on an installation to mark its owning user.</summary>
    internal static string UserTag(UserIdentifierType userId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{UserTagPrefix}{userId}");
}
