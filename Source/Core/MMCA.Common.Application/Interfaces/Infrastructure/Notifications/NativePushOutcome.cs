namespace MMCA.Common.Application.Interfaces.Infrastructure.Notifications;

/// <summary>
/// What one <see cref="INativePushSender.SendToUsersAsync"/> call achieved (ADR-044): per platform,
/// how many sends succeeded and how many failed, in the unit the provider reports, plus the first
/// failure. A sender returns this instead of throwing, so a failing platform is visible to the
/// caller without stopping the other platform or the other legs.
/// </summary>
public sealed record NativePushOutcome
{
    /// <summary>Gets the outcome of a call that attempted nothing (no adapter, or no installations).</summary>
    public static NativePushOutcome None { get; } = new();

    /// <summary>Gets the Android (FCM) counts, or null when nothing was attempted on Android.</summary>
    public NativePushPlatformCounts? Android { get; init; }

    /// <summary>Gets the Apple (APNs) counts, or null when nothing was attempted on Apple.</summary>
    public NativePushPlatformCounts? Apple { get; init; }

    /// <summary>Gets the unit the counts are expressed in, as the provider reports them.</summary>
    public NativePushDeliveryUnit Unit { get; init; }

    /// <summary>
    /// Gets the first failure the sender met, or null when nothing failed. Set whenever a send
    /// failed, including one the sender could not attribute to a platform (a failed installation
    /// lookup, say).
    /// </summary>
    public Exception? FirstFailure { get; init; }

    /// <summary>Gets a value indicating whether any send failed.</summary>
    public bool HasFailures => FirstFailure is not null || Android?.Failed > 0 || Apple?.Failed > 0;

    /// <summary>
    /// Builds an outcome from raw counts, leaving a platform null when nothing was attempted on it.
    /// </summary>
    /// <param name="unit">The unit the counts are expressed in.</param>
    /// <param name="androidSent">Android sends that succeeded.</param>
    /// <param name="androidFailed">Android sends that failed.</param>
    /// <param name="appleSent">Apple sends that succeeded.</param>
    /// <param name="appleFailed">Apple sends that failed.</param>
    /// <param name="firstFailure">The first failure, or null.</param>
    /// <returns>The outcome.</returns>
    public static NativePushOutcome From(
        NativePushDeliveryUnit unit,
        int androidSent,
        int androidFailed,
        int appleSent,
        int appleFailed,
        Exception? firstFailure) =>
        new()
        {
            Unit = unit,
            Android = androidSent + androidFailed == 0 ? null : new NativePushPlatformCounts(androidSent, androidFailed),
            Apple = appleSent + appleFailed == 0 ? null : new NativePushPlatformCounts(appleSent, appleFailed),
            FirstFailure = firstFailure,
        };
}
