using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using MMCA.Common.Shared.Identifiers;

namespace MMCA.Common.API.OpenApi;

/// <summary>
/// Renders a strongly typed identifier in the OpenAPI document as the primitive it wraps
/// (ADR-115), so a wrapped <c>OrderId</c> is documented as <c>{"type":"integer","format":"int32"}</c>
/// exactly like the <see langword="int"/> it replaced, rather than as an object with a
/// <c>value</c> property.
/// <para>
/// The default schema generator sees a struct with one public property and produces an object
/// schema, which would contradict the wire shape
/// <see cref="StronglyTypedIdJsonConverterFactory"/> actually produces and break every generated
/// client. This transformer rewrites the schema in place, keeping whatever description the source
/// carried, so the document says what the endpoint really accepts.
/// </para>
/// <para>
/// Registered once by <c>AddCommonOpenApi()</c> across every versioned document. It is inert in a
/// host that declares no wrappers.
/// </para>
/// </summary>
public sealed class StronglyTypedIdSchemaTransformer : IOpenApiSchemaTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (!StronglyTypedId.TryDescribe(context.JsonTypeInfo.Type, out _, out var valueType))
            return Task.CompletedTask;

        schema.Properties?.Clear();
        schema.Required?.Clear();
        schema.Type = MapType(valueType);
        schema.Format = MapFormat(valueType);

        return Task.CompletedTask;
    }

    internal static JsonSchemaType MapType(Type valueType)
        => valueType == typeof(int) || valueType == typeof(long)
            ? JsonSchemaType.Integer
            : JsonSchemaType.String;

    internal static string? MapFormat(Type valueType)
    {
        if (valueType == typeof(int))
            return "int32";

        if (valueType == typeof(long))
            return "int64";

        return valueType == typeof(Guid) ? "uuid" : null;
    }
}

/// <summary>
/// The route and query half of the same job. A schema transformer never sees a path or query
/// parameter typed as a wrapper: MVC's API explorer describes a parameter that binds through a
/// <see cref="System.ComponentModel.TypeConverter"/> as a plain string, so the document would say
/// <see langword="string"/> where the endpoint accepts an integer. This transformer rewrites those parameter
/// schemas from the action's own parameter types.
/// <para>
/// Registered once by <c>AddCommonOpenApi()</c> beside
/// <see cref="StronglyTypedIdSchemaTransformer"/>, and inert in a host that declares no wrappers.
/// </para>
/// </summary>
public sealed class StronglyTypedIdParameterTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (operation.Parameters is null)
            return Task.CompletedTask;

        foreach (var described in context.Description.ParameterDescriptions)
        {
            var parameterType = described.ModelMetadata?.ModelType ?? described.Type;
            if (parameterType is null || !StronglyTypedId.TryDescribe(parameterType, out _, out var valueType))
                continue;

            var parameter = operation.Parameters
                .OfType<OpenApiParameter>()
                .FirstOrDefault(p => string.Equals(p.Name, described.Name, StringComparison.Ordinal));

            if (parameter is null)
                continue;

            parameter.Schema = new OpenApiSchema
            {
                Type = StronglyTypedIdSchemaTransformer.MapType(valueType),
                Format = StronglyTypedIdSchemaTransformer.MapFormat(valueType),
            };
        }

        return Task.CompletedTask;
    }
}
