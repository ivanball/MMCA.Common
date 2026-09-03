using Microsoft.AspNetCore.Builder;

namespace MMCA.Common.API.Startup.Pipeline;

/// <summary>
/// One named step of the shared HTTP edge pipeline: a stable identifier plus the delegate that
/// registers the step's middleware on the application. Steps are pure data until
/// <c>UseCommonMiddlewarePipeline</c> runs them in order, which is what makes the pipeline order
/// testable without a running host.
/// </summary>
/// <param name="Name">
/// The step's identifier, used as the anchor for <see cref="MiddlewarePipelineBuilder"/> mutations.
/// Framework steps use the constants on <see cref="MiddlewarePipelineStepNames"/>; a host adding its
/// own step supplies its own name, which must be unique within the pipeline.
/// </param>
/// <param name="Configure">
/// Registers the step's middleware on the application. Invoked exactly once, in pipeline order, at
/// the point <c>UseCommonMiddlewarePipeline</c> is called, so anything the delegate reads from the
/// host (configuration, environment) is evaluated at configure time.
/// </param>
public sealed record MiddlewarePipelineStep(string Name, Action<WebApplication> Configure)
{
    /// <summary>
    /// The step's identifier, used as the anchor for <see cref="MiddlewarePipelineBuilder"/>
    /// mutations. Never null, empty, or whitespace.
    /// </summary>
    public string Name { get; init; } = Validated(Name);

    /// <summary>Registers the step's middleware on the application. Never null.</summary>
    public Action<WebApplication> Configure { get; init; } = Validated(Configure);

    private static string Validated(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }

    private static Action<WebApplication> Validated(Action<WebApplication> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return configure;
    }
}
