using MMCA.Common.Testing.UI;

namespace MMCA.Common.UI.Tests;

/// <summary>
/// Repo-local base for MMCA.Common UI component tests. Inherits the shared
/// <see cref="BunitComponentTestBase"/> (MudBlazor services, loose JSInterop, auth test doubles,
/// and the MudBlazor provider + interaction helpers) from the <c>MMCA.Common.Testing.UI</c> package.
/// Kept as a thin repo-local seam so Common-only service registrations can be added in one place.
/// </summary>
public abstract class BunitTestBase : BunitComponentTestBase;
