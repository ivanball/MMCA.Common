using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace MMCA.Common.UI.Components.Ratings;

/// <summary>
/// An editable star rating: <see cref="MudRating"/> inside a <c>role="radiogroup"</c> named by the
/// visible <see cref="Label"/>, with <see cref="ErrorText"/> linked through <c>aria-describedby</c>
/// while it is shown (WCAG 1.3.1 and 3.3.1). The read-only twin is <see cref="RatingStars"/>.
/// Bind it with <c>@bind-Value</c>; 0 means no star selected.
/// </summary>
public partial class RatingInput
{
    private readonly string _labelId = $"rating-label-{Guid.NewGuid():N}";
    private readonly string _errorId = $"rating-error-{Guid.NewGuid():N}";

    /// <summary>Gets or sets the visible caption, which is also the group's accessible name.</summary>
    [Parameter]
    [EditorRequired]
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected number of stars (0 for none).</summary>
    [Parameter]
    public int Value { get; set; }

    /// <summary>Gets or sets the callback invoked when the user selects a rating.</summary>
    [Parameter]
    public EventCallback<int> ValueChanged { get; set; }

    /// <summary>Gets or sets the number of stars. Defaults to <see cref="RatingStars.MaxValue"/>.</summary>
    [Parameter]
    public int MaxValue { get; set; } = RatingStars.MaxValue;

    /// <summary>
    /// Gets or sets the validation message. While it is non-empty it is rendered under the stars and
    /// linked to the group; set it to <see langword="null"/> to clear it.
    /// </summary>
    [Parameter]
    public string? ErrorText { get; set; }

    /// <summary>Gets or sets a value indicating whether the rating is disabled.</summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>Gets or sets the star size. Defaults to large, a comfortable touch target.</summary>
    [Parameter]
    public Size Size { get; set; } = Size.Large;

    /// <summary>Gets or sets the typography of the caption.</summary>
    [Parameter]
    public Typo LabelTypo { get; set; } = Typo.subtitle2;

    /// <summary>Gets or sets extra CSS classes for the group element.</summary>
    [Parameter]
    public string? Class { get; set; }

    private bool HasError => !string.IsNullOrEmpty(ErrorText);

    private Task OnSelectedValueChangedAsync(int value)
    {
        Value = value;
        return ValueChanged.InvokeAsync(value);
    }
}
