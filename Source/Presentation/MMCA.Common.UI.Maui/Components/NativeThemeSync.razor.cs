namespace MMCA.Common.UI.Maui.Components;

/// <summary>
/// Renders nothing. Bridges the Blazor Day/Dark preference to MAUI's own <c>AppTheme</c> so the native
/// chrome follows the in-app toggle, and mirrors the mode into device preferences for the next cold
/// launch. Hybrid heads render it by listing it in an <c>IUIModule</c>'s <c>LayoutComponentTypes</c>.
/// </summary>
/// <remarks>
/// The component body lives in <c>NativeThemeSync.razor</c>. This declaration exists to carry the XML
/// documentation: on the Windows target the WinRT source generator emits its own partial declaration
/// of every public type that implements a projected interface (<see cref="IDisposable"/> here), and
/// that generated part has no documentation comment, so without one on a hand-written part the
/// build fails with CS1591.
/// </remarks>
public partial class NativeThemeSync;
