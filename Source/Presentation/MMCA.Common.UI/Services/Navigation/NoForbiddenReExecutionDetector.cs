namespace MMCA.Common.UI.Services.Navigation;

/// <summary>
/// The default <see cref="IForbiddenReExecutionDetector"/> for heads that never render a server
/// response (Blazor WebAssembly, MAUI): there is no request to read, so a render is never a 403
/// re-execution.
/// </summary>
internal sealed class NoForbiddenReExecutionDetector : IForbiddenReExecutionDetector
{
    /// <inheritdoc />
    public bool IsForbiddenReExecution => false;
}
