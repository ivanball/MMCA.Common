using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace MMCA.Common.UI.Components.Ratings;

/// <summary>
/// Five read-only stars for a rating, rendered as a single <c>role="img"</c> element whose
/// accessible name is the caller's label (the numeric figure belongs there, the stars are
/// decorative rounding). Whole stars fill, a half star renders at .5 and above, the rest are
/// outlines. Each icon carries <c>data-star="full|half|empty"</c> and the wrapper
/// <c>data-testid="rating-stars"</c>.
/// </summary>
public partial class RatingStars
{
    /// <summary>The number of stars a rating is measured out of.</summary>
    public const int MaxValue = 5;

    private const string FullStar = "full";
    private const string HalfStar = "half";
    private const string EmptyStar = "empty";

    /// <summary>
    /// Gets or sets the rating to render, on a 0 to <see cref="MaxValue"/> scale. A caller holding a
    /// <see cref="decimal"/> average casts it (<c>(double)average</c>); the half-star threshold does
    /// not need decimal precision.
    /// </summary>
    [Parameter]
    public double Value { get; set; }

    /// <summary>Gets or sets the accessible name announced for the whole rating.</summary>
    [Parameter]
    [EditorRequired]
    public string AriaLabel { get; set; } = string.Empty;

    /// <summary>Gets or sets the icon size.</summary>
    [Parameter]
    public Size Size { get; set; } = Size.Small;

    private static string IconFor(string fill) => fill switch
    {
        FullStar => Icons.Material.Filled.Star,
        HalfStar => Icons.Material.Filled.StarHalf,
        _ => Icons.Material.Filled.StarBorder,
    };

    private string FillFor(int star)
    {
        if (Value >= star)
        {
            return FullStar;
        }

        return Value >= star - 0.5 ? HalfStar : EmptyStar;
    }
}
