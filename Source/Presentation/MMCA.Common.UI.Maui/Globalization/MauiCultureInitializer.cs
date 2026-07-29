namespace MMCA.Common.UI.Maui.Globalization;

/// <summary>
/// Restores the persisted culture when the MAUI app is built (ADR-027), the hybrid counterpart to the
/// WASM head's <c>MmcaCultureBootstrap</c>. <c>IMauiInitializeService.Initialize</c> runs inside
/// <c>MauiAppBuilder.Build()</c>, before any window or page exists, so the very first Blazor render
/// already happens under the right culture: no flash of the wrong language, and no switch needed on
/// every launch just to get back to the language the user picked last time.
/// <para>
/// Without this, a hybrid head has no culture state of its own and always starts at the device locale,
/// which is why persisting the choice in <see cref="MauiCultureApplier"/> alone is not enough.
/// </para>
/// </summary>
public sealed class MauiCultureInitializer : IMauiInitializeService
{
    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="services"/> is unused: the culture lives in device preferences and process state,
    /// both reachable without DI. The parameter belongs to the interface, not to this restore.
    /// </remarks>
    public void Initialize(IServiceProvider services) =>
        MauiCultureStore.ApplyToProcess(MauiCultureStore.Resolve());
}
