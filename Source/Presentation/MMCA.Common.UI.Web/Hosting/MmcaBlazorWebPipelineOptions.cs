using Microsoft.AspNetCore.Builder;
using MMCA.Common.UI.Web.ClientConfig;

namespace MMCA.Common.UI.Web.Hosting;

/// <summary>
/// Options for <c>UseMmcaBlazorWebPipeline</c>: the parts of the request pipeline that differ between
/// Blazor heads.
/// </summary>
public sealed class MmcaBlazorWebPipelineOptions
{
    /// <summary>
    /// Runs in Development only, at the exception-handler position (outside Development that position is
    /// <c>UseExceptionHandler("/Error")</c>). A net10.0 head with a WebAssembly client passes
    /// <c>a =&gt; a.UseWebAssemblyDebugging()</c>; a net11.0 head passes nothing.
    /// </summary>
    public Action<IApplicationBuilder>? DevelopmentPipeline { get; init; }

    /// <summary>
    /// Adds the head's own sections to the client-config document (the extras callback of
    /// <c>MapClientConfigEndpoint</c>).
    /// </summary>
    public Action<ClientConfigBuilder>? ClientConfig { get; init; }
}
