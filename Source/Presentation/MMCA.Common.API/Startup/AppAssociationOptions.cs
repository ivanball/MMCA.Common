namespace MMCA.Common.API.Startup;

/// <summary>
/// Options for <see cref="AppAssociationEndpointExtensions"/>: the identifiers the mobile OS uses to
/// verify that the installed app may handle this host's https links. Hosts typically bind these from
/// an <c>AppAssociation</c> configuration section so a certificate rotation is a config change, not
/// a code change.
/// </summary>
public sealed class AppAssociationOptions
{
    /// <summary>The Android application id (package name) declared in <c>assetlinks.json</c>.</summary>
    public required string AndroidPackageName { get; init; }

    /// <summary>
    /// SHA-256 signing-certificate fingerprints for the Android app. For Play-distributed builds this
    /// is the Play App Signing certificate, NOT the local upload keystore.
    /// </summary>
    public IReadOnlyList<string> AndroidCertFingerprints { get; init; } = [];

    /// <summary>The Apple app identifier (<c>TeamID.BundleID</c>) used by both <c>webcredentials</c> and <c>applinks</c>.</summary>
    public required string AppleAppId { get; init; }

    /// <summary>
    /// URL patterns for the <c>applinks</c> components list (e.g. <c>"/conference/*"</c>); each entry
    /// becomes a <c>{ "/": pattern }</c> component. Mirror the app's shared Blazor routes: identical
    /// URLs on web and device is the Blazor Hybrid payoff (no route translation table).
    /// </summary>
    public IReadOnlyList<string> AppleAppLinkComponents { get; init; } = [];
}
