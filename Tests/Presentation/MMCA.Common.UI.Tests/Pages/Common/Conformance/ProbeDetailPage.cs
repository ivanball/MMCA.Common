using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Components.PageState;
using MMCA.Common.UI.Pages.Common;

namespace MMCA.Common.UI.Tests.Pages.Common.Conformance;

/// <summary>The record a <see cref="ProbeDetailPage"/> loads and renders.</summary>
/// <param name="Id">The record id.</param>
/// <param name="Name">The display text the page renders once the record has loaded.</param>
public sealed record ProbeRecord(int Id, string Name);

/// <summary>The client service a <see cref="ProbeDetailPage"/> fetches its record through (a Moq double in the tests).</summary>
public interface IProbeRecordClient
{
    /// <summary>Fetches the record with <paramref name="id"/>.</summary>
    /// <param name="id">The record id.</param>
    /// <param name="cancellationToken">The load's token.</param>
    /// <returns>The record, or the failure the page renders.</returns>
    Task<Result<ProbeRecord>> GetAsync(int id, CancellationToken cancellationToken);
}

/// <summary>
/// A consumer-shaped detail page wired the way the framework expects: it derives
/// <see cref="DetailPageBase"/>, loads by route id through <see cref="DetailPageBase.LoadGuard"/>
/// (via <c>BeginLoad</c> / <c>CompleteLoad</c>), and renders <see cref="DetailLoadStateContent"/> for
/// every state but Loaded. The two virtual members exist only so the failure-proof tests can derive a
/// deliberately broken page; the conforming page keeps their defaults.
/// </summary>
public class ProbeDetailPage : DetailPageBase
{
    /// <summary>The text the page renders for a record that does not exist.</summary>
    public const string NotFoundText = "No such probe record.";

    /// <summary>The accessible label the page gives its loading spinner.</summary>
    public const string LoadingText = "Loading probe record";

    private ProbeRecord? _record;

    /// <summary>Gets or sets the route id of the record to load.</summary>
    [Parameter]
    public int Id { get; set; }

    [Inject]
    private IProbeRecordClient Client { get; set; } = default!;

    /// <summary>
    /// Gets a value indicating whether the page adopts only the result <c>CompleteLoad</c> accepts.
    /// A broken override adopts every successful result, including one a newer route id superseded.
    /// </summary>
    protected virtual bool HonorsLoadGuard => true;

    /// <summary>Maps the page's load state to the state it renders. A broken override collapses Failed into NotFound.</summary>
    /// <param name="state">The page's load state.</param>
    /// <returns>The state handed to <see cref="DetailLoadStateContent"/>.</returns>
    protected virtual DetailLoadState RenderedState(DetailLoadState state) => state;

    /// <inheritdoc />
    protected override Task OnParametersSetAsync() => LoadAsync();

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (LoadState == DetailLoadState.Loaded && _record is not null)
        {
            builder.OpenElement(0, "h1");
            builder.AddContent(1, _record.Name);
            builder.CloseElement();
            return;
        }

        builder.OpenComponent<DetailLoadStateContent>(2);
        builder.AddComponentParameter(3, nameof(DetailLoadStateContent.State), RenderedState(LoadState));
        builder.AddComponentParameter(4, nameof(DetailLoadStateContent.LoadingLabel), LoadingText);
        builder.AddComponentParameter(5, nameof(DetailLoadStateContent.NotFoundMessage), NotFoundText);
        builder.AddComponentParameter(6, nameof(DetailLoadStateContent.OnRetry), EventCallback.Factory.Create(this, LoadAsync));
        builder.CloseComponent();
    }

    private async Task LoadAsync()
    {
        var (token, generation) = BeginLoad();
        var result = await Client.GetAsync(Id, token).ConfigureAwait(true);

        var adopt = CompleteLoad(generation, result);
        if (adopt || !HonorsLoadGuard && result.IsSuccess)
        {
            _record = result.Value;
        }
    }
}
