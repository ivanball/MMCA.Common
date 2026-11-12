using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.MachineClients;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Services.Administration;

namespace MMCA.Common.UI.Pages.Administration;

/// <summary>
/// The administration screen of machine clients (ADR-140): the clients with their last use and status,
/// a create form, and rotate, promote and revoke actions. A secret the API returns (on create or
/// rotate) is shown once, in a dismissible notice, and never again. It is a COMPONENT, not a page: the
/// app supplies the route and the authorization attribute (<c>clients:manage</c>), as with
/// <see cref="RoleAdminList"/>.
/// </summary>
/// <remarks>
/// Every string is looked up in <see cref="Localizer"/> FIRST and falls back to
/// <see cref="MachineClientAdminResources"/>, the <see cref="RoleAdminList"/> contract.
/// </remarks>
public partial class MachineClientAdmin : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private List<MachineClientResponse> _clients = [];
    private MachineClientSecretResponse? _issued;
    private string? _name;
    private string? _role;
    private bool _saving;
    private bool _disposed;

    /// <summary>The last load's (or row action's) outcome; a failure is rendered inline.</summary>
    private Result? _loadResult;

    /// <summary>The last create's outcome, rendered inside the form.</summary>
    private Result? _saveResult;

    /// <summary>The page title. Defaults to the localized "Machine clients" when not supplied.</summary>
    [Parameter] public string? Heading { get; set; }

    /// <summary>
    /// An app localizer consulted FIRST for every key this component renders; a key it does not
    /// carry falls back to <see cref="MachineClientAdminResources"/>.
    /// </summary>
    [Parameter] public IStringLocalizer? Localizer { get; set; }

    /// <summary>True while the initial (or retried) load is in flight.</summary>
    protected bool IsLoading { get; private set; }

    [Inject] private IMachineClientAdminUIService Clients { get; set; } = default!;

    [Inject] private IStringLocalizer<MachineClientAdminResources> L { get; set; } = default!;

    private string Title => Heading ?? T("Title");

    /// <summary>
    /// True when the list load answered not found with nothing loaded. Every machine-client route
    /// answers 404 while <c>MachineClients:Enabled</c> is false on the server, so from here that is what
    /// "not enabled" looks like; the component then renders a notice in place of the form and the list.
    /// </summary>
    private bool NotEnabled => _clients.Count == 0 && _loadResult is { IsFailure: true } failure && failure.IsNotFound();

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadAsync();

    /// <summary>Releases the cancellation source that supersedes an in-flight call when the component goes away.</summary>
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

    /// <summary>Resolves one key: the app's localizer when it carries the key, otherwise this component's resources.</summary>
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

    private string StatusText(MachineClientResponse client) => client switch
    {
        { RevokedOn: not null } => T("Status.Revoked"),
        { HasSecondarySecret: true } => T("Status.RotationPending"),
        _ => T("Status.Active"),
    };

    private async Task LoadAsync()
    {
        IsLoading = true;
        _loadResult = null;

        try
        {
            var result = await Clients.GetAllAsync(_cts.LifetimeToken());
            if (result.TryGetValue(out var clients))
            {
                _clients = [.. clients];
                _loadResult = Result.Success();
            }
            else
            {
                _loadResult = Result.Failure(result.Errors);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CreateAsync()
    {
        _saving = true;
        try
        {
            var result = await Clients.CreateAsync(
                new CreateMachineClientRequest(_name ?? string.Empty, _role ?? string.Empty),
                _cts.LifetimeToken());
            if (result.TryGetValue(out var issued))
            {
                _issued = issued;
                _clients.Add(issued.Client);
                _name = null;
                _role = null;
                _saveResult = null;
            }
            else
            {
                _saveResult = Result.Failure(result.Errors);
            }
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task RotateAsync(MachineClientResponse client)
    {
        var result = await Clients.RotateAsync(client.ClientId, _cts.LifetimeToken());
        if (result.TryGetValue(out var issued))
        {
            _issued = issued;
            Replace(issued.Client);
        }
        else
        {
            _loadResult = Result.Failure(result.Errors);
        }
    }

    private async Task PromoteAsync(MachineClientResponse client) =>
        ApplyRowResult(await Clients.PromoteAsync(client.ClientId, _cts.LifetimeToken()));

    private async Task RevokeAsync(MachineClientResponse client) =>
        ApplyRowResult(await Clients.RevokeAsync(client.ClientId, _cts.LifetimeToken()));

    private void ApplyRowResult(Result<MachineClientResponse> result)
    {
        if (result.TryGetValue(out var updated))
        {
            Replace(updated);
        }
        else
        {
            _loadResult = Result.Failure(result.Errors);
        }
    }

    private void DismissSecret() => _issued = null;

    private void Replace(MachineClientResponse updated)
    {
        var index = _clients.FindIndex(c => c.ClientId == updated.ClientId);
        if (index >= 0)
        {
            _clients[index] = updated;
        }
    }
}
