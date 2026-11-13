using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.ServiceHost;

/// <summary>
/// The composed host's side of the OpenAPI document: the name of the document it pins to 3.1, and the
/// startup check that the host registered that document itself.
/// </summary>
internal static class ServiceHostOpenApi
{
    /// <summary>
    /// The document a bare <c>AddOpenApi()</c> registers and <c>MapCommonOpenApi()</c> serves.
    /// </summary>
    internal const string DocumentName = "v1";

    /// <summary>
    /// Throws when the host never called <c>AddOpenApi()</c>, detected by the keyed
    /// <see cref="IOpenApiDocumentProvider"/> that <c>AddOpenApi</c> registers for <see cref="DocumentName"/>
    /// (the registration <c>MapCommonOpenApi()</c> checks too).
    /// </summary>
    /// <param name="services">The application's root service provider.</param>
    /// <exception cref="InvalidOperationException">No <c>v1</c> document is registered.</exception>
    internal static void EnsureHostRegisteredDocument(IServiceProvider services)
    {
        var keyed = services.GetService<IServiceProviderIsKeyedService>();
        if (keyed?.IsKeyedService(typeof(IOpenApiDocumentProvider), DocumentName) == true)
        {
            return;
        }

        throw new InvalidOperationException(
            "UseMmcaServiceHostAsync found no '" + DocumentName + "' OpenAPI document. Call "
            + "builder.Services.AddOpenApi() in the host's own Program.cs: the OpenAPI XML-comment "
            + "generator attaches the controller summaries from the host project's XML comments only at "
            + "an AddOpenApi call compiled in that project, so AddMmcaServiceHost does not register the "
            + "document itself (it pins the version to OpenAPI 3.1 and adds AddCommonOpenApi()).");
    }
}
