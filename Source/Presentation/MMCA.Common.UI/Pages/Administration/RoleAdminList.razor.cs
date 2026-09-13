using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Administration;

namespace MMCA.Common.UI.Pages.Administration;

/// <summary>
/// The roster half of ADR-116's role administration: one row per role the host knows, with the
/// number of permissions its code grants, the number stored rows grant, and a link to the editor.
/// It is a COMPONENT, not a page: the app supplies the route, the authorization attribute and the
/// edit link, so the role that may reach it stays the app's decision.
/// </summary>
/// <remarks>
/// <para>
/// The two counts are reported apart because only one of them is editable. A single total would
/// hide the fact that removing a compiled permission is a code change, which is the one thing an
/// operator has to understand before opening the editor.
/// </para>
/// <para>
/// Every string it renders is looked up in <see cref="Localizer"/> FIRST and falls back to
/// <see cref="RoleAdminListResources"/>, so an app keeps its own vocabulary without a parameter per
/// word. That is the <see cref="UserAdminList{TUser}"/> contract, unchanged.
/// </para>
/// </remarks>
public partial class RoleAdminList : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private IReadOnlyList<RolePermissionsResponse> _roles = [];
    private bool _disposed;

    /// <summary>The page title. Defaults to the localized "Roles" when not supplied.</summary>
    [Parameter] public string? Heading { get; set; }

    /// <summary>Builds the route to one role's editor. Required.</summary>
    [Parameter]
    [EditorRequired]
    public Func<string, string> EditHref { get; set; } = default!;

    /// <summary>
    /// An app localizer consulted FIRST for every key this component renders; a key it does not
    /// carry falls back to <see cref="RoleAdminListResources"/>.
    /// </summary>
    [Parameter] public IStringLocalizer? Localizer { get; set; }

    [Inject] private IRoleAdminUIService Roles { get; set; } = default!;

    [Inject] private IStringLocalizer<RoleAdminListResources> L { get; set; } = default!;

    /// <summary>True while the initial (or retried) load is in flight.</summary>
    protected bool IsLoading { get; private set; }

    private string Title => Heading ?? T("Title");

    /// <summary>The last load attempt's outcome; a failure is rendered inline with a retry.</summary>
    private Result? _loadResult;

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadRolesAsync();

    /// <summary>
    /// Releases the cancellation source that supersedes an in-flight load when the component goes
    /// away.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// Resolves one key: the app's own localizer when it carries the key, otherwise this
    /// component's resources. Arguments go through the localizer's indexer rather than
    /// <c>string.Format</c>, so a translation is free to reorder its placeholders.
    /// </summary>
    /// <param name="key">The resource key.</param>
    /// <param name="args">Format arguments, if the string takes any.</param>
    /// <returns>The resolved string.</returns>
    private string T(string key, params object[] args)
    {
        if (Localizer is not null)
        {
            var overridden = args.Length == 0 ? Localizer[key] : Localizer[key, args];
            if (!overridden.ResourceNotFound)
            {
                return overridden.Value;
            }
        }

        return (args.Length == 0 ? L[key] : L[key, args]).Value;
    }

    private async Task LoadRolesAsync()
    {
        IsLoading = true;
        _loadResult = null;

        try
        {
            var result = await Roles.GetAllAsync(_cts.Token);
            _loadResult = result;
            _roles = result.IsSuccess ? result.Value! : [];
        }
        finally
        {
            IsLoading = false;
        }
    }
}
