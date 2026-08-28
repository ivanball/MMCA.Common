using Microsoft.AspNetCore.Components;

namespace MMCA.Common.UI.Services;

/// <summary>
/// Default <see cref="IPublicLinkBuilder"/>: resolves against the browser origin
/// (<see cref="NavigationManager.BaseUri"/>), correct for the Server and WebAssembly heads.
/// The MAUI head overrides this registration with its configured public site URL
/// (<c>AddCommonMauiPublicLinkBuilder()</c> in MMCA.Common.UI.Maui).
/// </summary>
public sealed class NavigationPublicLinkBuilder : IPublicLinkBuilder
{
    private readonly NavigationManager _navigationManager;

    /// <summary>Initializes the builder over the host's navigation manager.</summary>
    /// <param name="navigationManager">The host's navigation manager, supplying the browser origin.</param>
    public NavigationPublicLinkBuilder(NavigationManager navigationManager) =>
        _navigationManager = navigationManager;

    /// <inheritdoc />
    public Uri BuildAbsolute(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        return new Uri(new Uri(_navigationManager.BaseUri, UriKind.Absolute), relativePath);
    }
}
