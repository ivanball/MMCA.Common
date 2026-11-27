namespace MMCA.Common.UI.Components.Lists;

/// <summary>
/// What <see cref="ListPageShell{TItem}"/> reads from the page that owns its <c>ServerData</c>
/// callback when the page does not pass <see cref="ListPageShell{TItem}.IsLayoutKnown"/> itself.
/// <c>DataGridListPageBase</c> implements it, so every list page wired through the shell holds back
/// both layouts until its first viewport notification without a markup change.
/// </summary>
internal interface IListLayoutSource
{
    /// <summary>Gets a value indicating whether the page has learned its viewport and chosen grid or card list.</summary>
    bool IsLayoutKnown { get; }
}
