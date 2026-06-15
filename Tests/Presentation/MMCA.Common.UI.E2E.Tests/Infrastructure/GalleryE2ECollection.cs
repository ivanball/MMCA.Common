using MMCA.Common.Testing.E2E.Infrastructure;
using Xunit;

namespace MMCA.Common.UI.E2E.Tests.Infrastructure;

/// <summary>
/// Shares a single launched browser (<see cref="PlaywrightFixture"/>) and a single self-hosted gallery
/// (<see cref="GalleryHostFixture"/>) across every E2E test, so the browser and host start once.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GalleryE2ECollection :
    ICollectionFixture<PlaywrightFixture>,
    ICollectionFixture<GalleryHostFixture>
{
    public const string Name = "Gallery E2E";
}
