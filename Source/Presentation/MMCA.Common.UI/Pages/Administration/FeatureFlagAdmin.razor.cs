using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.FeatureFlags;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Services.Administration;

namespace MMCA.Common.UI.Pages.Administration;

/// <summary>
/// The administration screen of the reloadable feature flags (ADR-031): one table of every flag the
/// configured flag sources declare, with what configuration says, the stored override and its reason,
/// and which is in force, plus set and clear actions. It is a COMPONENT, not a page: the app supplies
/// the route and the authorization attribute (<c>features:manage</c>), as with
/// <see cref="RoleAdminList"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Several sources, one table.</b> Each service of a multi-service host owns its overrides in its
/// own database and serves them under its own route prefix, so the component reads every prefix in
/// <see cref="Sources"/> (or, when that is not supplied, the ones <c>AddFeatureFlagAdministrationUI</c>
/// configured) and sends each set or clear to the source that listed the flag.
/// </para>
/// <para>
/// Every string is looked up in <see cref="Localizer"/> FIRST and falls back to
/// <see cref="FeatureFlagAdminResources"/>, the <see cref="RoleAdminList"/> contract.
/// </para>
/// </remarks>
public partial class FeatureFlagAdmin : ComponentBase, IDisposable
{
    private static readonly FeatureFlagOverrideMode[] Modes = Enum.GetValues<FeatureFlagOverrideMode>();

    private readonly CancellationTokenSource _cts = new();

    private List<FlagRow> _rows = [];
    private FlagRow? _editing;
    private FeatureFlagOverrideMode _mode;
    private int? _percentage;
    private string? _users;
    private string? _groups;
    private string? _reason;
    private bool _saving;
    private bool _disposed;

    /// <summary>The last load's outcome; a failure is rendered inline with a retry.</summary>
    private Result? _loadResult;

    /// <summary>The last save's outcome, rendered inside the editor.</summary>
    private Result? _saveResult;

    /// <summary>
    /// The API route prefixes to list flags from, in display order. Defaults to the sources
    /// <c>AddFeatureFlagAdministrationUI</c> configured.
    /// </summary>
    [Parameter] public IReadOnlyList<string>? Sources { get; set; }

    /// <summary>The page title. Defaults to the localized "Feature flags" when not supplied.</summary>
    [Parameter] public string? Heading { get; set; }

    /// <summary>
    /// An app localizer consulted FIRST for every key this component renders; a key it does not
    /// carry falls back to <see cref="FeatureFlagAdminResources"/>.
    /// </summary>
    [Parameter] public IStringLocalizer? Localizer { get; set; }

    /// <summary>True while the initial (or retried) load is in flight.</summary>
    protected bool IsLoading { get; private set; }

    [Inject] private IFeatureFlagAdminUIService Flags { get; set; } = default!;

    [Inject] private IStringLocalizer<FeatureFlagAdminResources> L { get; set; } = default!;

    [Inject] private IOptions<FeatureFlagAdminUIOptions> ConfiguredSources { get; set; } = default!;

    private IReadOnlyList<string> EffectiveSources => Sources ?? ConfiguredSources.Value.Sources;

    /// <summary>The source column only earns its width when there is more than one source.</summary>
    private bool ShowSource => EffectiveSources.Count > 1;

    private string Title => Heading ?? T("Title");

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadAsync();

    /// <summary>
    /// Releases the cancellation source that supersedes an in-flight call when the component goes
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

    private static List<string> SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

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

    private string LifetimeText(FeatureFlagLifetime? lifetime) =>
        lifetime is { } declared ? T($"Lifetime.{declared}") : T("Lifetime.Unknown");

    private string ValueText(FeatureFlagValueKind kind) => T($"Value.{kind}");

    private string ModeText(FeatureFlagOverrideMode mode) => T($"Mode.{mode}");

    private async Task LoadAsync()
    {
        IsLoading = true;
        _loadResult = null;

        try
        {
            var rows = new List<FlagRow>();
            Result? firstFailure = null;

            foreach (var source in EffectiveSources)
            {
                var result = await Flags.GetAllAsync(source, _cts.LifetimeToken());
                if (result.TryGetValue(out var flags))
                {
                    rows.AddRange(flags.Select(flag => new FlagRow(source, flag)));
                }
                else
                {
                    firstFailure ??= Result.Failure(result.Errors);
                }
            }

            _rows = rows;
            _loadResult = firstFailure ?? Result.Success();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void BeginEdit(FlagRow row)
    {
        var stored = row.Flag.Override;
        _editing = row;
        _mode = stored?.Mode ?? FeatureFlagOverrideMode.Off;
        _percentage = stored?.Percentage;
        _users = stored is null ? null : string.Join(", ", stored.Users);
        _groups = stored is null ? null : string.Join(", ", stored.Groups);

        // A fresh reason every time: the reason belongs to THIS change, not the one before it.
        _reason = null;
        _saveResult = null;
    }

    private void CancelEdit()
    {
        _editing = null;
        _saveResult = null;
    }

    private async Task SaveAsync()
    {
        if (_editing is not { } row)
        {
            return;
        }

        _saving = true;
        try
        {
            var request = new SetFeatureFlagOverrideRequest(
                _mode,
                _reason ?? string.Empty,
                _percentage,
                SplitList(_users),
                SplitList(_groups));

            var result = await Flags.SetOverrideAsync(row.Source, row.Flag.Name, request, _cts.LifetimeToken());
            if (result.TryGetValue(out var updated))
            {
                Replace(row, row with { Flag = updated });
                _editing = null;
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

    private async Task ClearAsync(FlagRow row)
    {
        var result = await Flags.ClearOverrideAsync(row.Source, row.Flag.Name, _cts.LifetimeToken());
        if (result.IsFailure)
        {
            _loadResult = result;
            return;
        }

        Replace(row, row with { Flag = row.Flag with { Override = null, Effective = row.Flag.Configured } });
    }

    private void Replace(FlagRow current, FlagRow updated)
    {
        var index = _rows.IndexOf(current);
        if (index >= 0)
        {
            _rows[index] = updated;
        }
    }

    /// <summary>One table row: the flag and the source that declared it.</summary>
    /// <param name="Source">The API route prefix the flag was listed from.</param>
    /// <param name="Flag">The flag.</param>
    private sealed record FlagRow(string Source, FeatureFlagStatusResponse Flag);
}
