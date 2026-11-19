namespace MMCA.Common.UI.Services.Navigation;

/// <summary>
/// Answers whether the page being rendered is a status-code re-execution of a request the server
/// already answered with 403 Forbidden.
/// <para>
/// Hosts re-execute error statuses to <c>/not-found</c>
/// (<c>UseStatusCodePagesWithReExecute("/not-found")</c>), so a full page load refused with 403 (a
/// signed-in user lacking the permission) also lands on the not-found page, with the original status
/// still on the response. Only a server render can see that response, and MMCA.Common.UI must stay
/// free of server types for Blazor WebAssembly, so the question sits behind this contract:
/// <c>AddUIShared</c> registers a default that always answers <see langword="false"/> (WebAssembly,
/// MAUI and any interactive render), and MMCA.Common.UI.Web's <c>AddMmcaBlazorWebHost</c> replaces it
/// with one that reads the current request's status.
/// </para>
/// </summary>
public interface IForbiddenReExecutionDetector
{
    /// <summary>
    /// Gets a value indicating whether this render answers a request whose response status is already
    /// 403 Forbidden; <see langword="false"/> whenever there is no request to read.
    /// </summary>
    bool IsForbiddenReExecution { get; }
}
