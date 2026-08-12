using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;

namespace MMCA.Common.API.OpenApi;

/// <summary>
/// Backfills a placeholder <see cref="ParameterDescriptor"/> onto every
/// <see cref="ApiParameterDescription"/> the API explorer leaves with a
/// <see langword="null"/> one, so downstream OpenAPI transformers that assume the property is
/// always populated cannot fail the document.
/// </summary>
/// <remarks>
/// <para>
/// MVC's <c>DefaultApiDescriptionProvider</c> creates an <see cref="ApiParameterDescription"/> for
/// every route-template token. When a token has no matching action parameter there is no
/// descriptor to attach, so <see cref="ApiParameterDescription.ParameterDescriptor"/> stays
/// <see langword="null"/>. That is normal, documented MVC behaviour for a route such as
/// <c>[Route("api/v{version:apiVersion}/orders")]</c> or <c>[Route("api/{tenant}/orders")]</c>.
/// </para>
/// <para>
/// <c>Asp.Versioning.OpenApi</c> 10.2.1 does not account for it:
/// <c>XmlCommentsTransformer.TransformAsync(OpenApiOperation, ...)</c> reads
/// <c>arg.ParameterDescriptor.Name</c> without a null check, so the first URL-segment-versioned
/// route turns <c>GET /openapi/{documentName}.json</c> into a <c>500</c> with a
/// <see cref="NullReferenceException"/>. Because <c>AddCommonApiVersioning</c> enables
/// <c>SubstituteApiVersionInUrl</c>, any consumer that adopts segment versioning would hit this.
/// </para>
/// <para>
/// The guard is deliberately general rather than special-casing the API version token: the null is
/// a property of unbound route tokens, not of versioning, so a <c>{tenant}</c> or <c>{region}</c>
/// segment fails the same way. Filling the gap here fixes it once for every consumer of the API
/// explorer instead of only for the one transformer that happens to dereference it today.
/// </para>
/// <para>
/// It runs with <see cref="Order"/> at <see cref="int.MinValue"/>. Providers execute
/// <c>OnProvidersExecuted</c> in DESCENDING order, so the lowest order runs last and observes
/// every parameter any other provider contributed, including the ones
/// <c>VersionedApiDescriptionProvider</c> adds. Existing descriptors are never replaced, so the
/// guard is inert on a fixed upstream package and can be deleted without a behaviour change once
/// the upstream null check lands.
/// </para>
/// </remarks>
internal sealed class ApiParameterDescriptorBackfillProvider : IApiDescriptionProvider
{
    /// <inheritdoc />
    public int Order => int.MinValue;

    /// <inheritdoc />
    public void OnProvidersExecuting(ApiDescriptionProviderContext context)
    {
        // Nothing to do before the descriptions are built: the parameters this guard repairs do
        // not exist yet.
    }

    /// <inheritdoc />
    public void OnProvidersExecuted(ApiDescriptionProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var parameter in context.Results.SelectMany(description => description.ParameterDescriptions))
        {
            // Only fill the gap. A descriptor supplied by MVC or by API versioning is richer than
            // anything reconstructable here (ControllerParameterDescriptor carries the
            // ParameterInfo that XML-comment lookups prefer), so it always wins.
            parameter.ParameterDescriptor ??= new ParameterDescriptor
            {
                Name = parameter.Name ?? string.Empty,
                ParameterType = parameter.Type
                    ?? parameter.ModelMetadata?.ModelType
                    ?? typeof(string),
            };
        }
    }
}
