namespace MMCA.Common.UI.Services.Capabilities;

/// <summary>Event payload carrying an app-relative route requested by a native deep-link source.</summary>
public sealed class DeepLinkRouteEventArgs : EventArgs
{
    /// <summary>Initializes the payload with the requested route.</summary>
    public DeepLinkRouteEventArgs(string route) => Route = route;

    /// <summary>App-relative route to navigate to (e.g. <c>/happening-now</c>).</summary>
    public string Route { get; }
}
