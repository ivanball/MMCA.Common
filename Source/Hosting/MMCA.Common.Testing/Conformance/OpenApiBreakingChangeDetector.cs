using System.Globalization;
using System.Text.Json;

namespace MMCA.Common.Testing.Conformance;

/// <summary>
/// Compares a committed baseline OpenAPI document with the live one and reports only the changes that
/// break an existing client: a removed path or operation, a removed response status code, a request
/// property newly required, a removed response property, a changed property type, a removed parameter,
/// a parameter newly required, a new required parameter, a changed parameter type, a removed request
/// body, a new required request body, or a removed request media type. Everything else passes,
/// additive changes included.
/// </summary>
/// <remarks>
/// <para>
/// Both documents are normalized by reading them structurally rather than textually: paths, operations,
/// status codes and properties are matched by name, so property order never shows up as a change, and
/// only the contract-bearing members are read
/// (<c>paths, parameters, in, name, responses, requestBody, content, schema, $ref,</c>
/// <c>type, properties, required, items</c>), so descriptions, summaries, examples, titles, tags and
/// every other documentation-only field are ignored by construction.
/// </para>
/// <para>
/// A parameter is identified by its location and its name, the name compared case-insensitively, and
/// an operation's parameters are its path item's shared ones overridden by its own. A path parameter is
/// always required. Parameter and request-body findings carry a <see cref="OpenApiBreakingChange.Detail"/>
/// naming the method, the path and the parameter or body (for example
/// <c>GET /Orders: query parameter 'page'</c>), and their pointer is the declaration site in the
/// operation, so the same shared component reached from two operations is reported for each.
/// </para>
/// <para>
/// Schemas are followed through local <c>$ref</c>s, and a component reached from several operations is
/// reported once. Request bodies are compared in the request direction (a newly required property
/// breaks a caller) and responses in the response direction (a removed property breaks a reader);
/// a type change breaks either way.
/// </para>
/// </remarks>
public static class OpenApiBreakingChangeDetector
{
    /// <summary>Finds the breaking changes from <paramref name="baselineJson"/> to <paramref name="liveJson"/>.</summary>
    /// <param name="baselineJson">The committed baseline OpenAPI document.</param>
    /// <param name="liveJson">The document the host serves now.</param>
    /// <returns>The breaking changes, ordered by pointer then kind; empty when the live document is compatible.</returns>
    public static IReadOnlyList<OpenApiBreakingChange> Detect(string baselineJson, string liveJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveJson);

        using var baseline = JsonDocument.Parse(baselineJson);
        using var live = JsonDocument.Parse(liveJson);

        var comparison = new Comparison(baseline.RootElement, live.RootElement);
        comparison.ComparePaths();

        return [.. comparison.Changes
            .OrderBy(change => change.JsonPointer, StringComparer.Ordinal)
            .ThenBy(change => change.Kind)];
    }

    /// <summary>One baseline-to-live comparison: the two roots, the findings, and the visited schema pairs.</summary>
    private sealed class Comparison(JsonElement baselineRoot, JsonElement liveRoot)
    {
        private static readonly string[] OperationNames = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

        private readonly HashSet<string> _visited = [];

        private enum Direction
        {
            Request,
            Response,
        }

        public HashSet<OpenApiBreakingChange> Changes { get; } = [];

        public void ComparePaths()
        {
            if (!TryGetObject(baselineRoot, "paths", out var baselinePaths))
            {
                return;
            }

            TryGetObject(liveRoot, "paths", out var livePaths);
            foreach (var path in baselinePaths.EnumerateObject())
            {
                var pointer = "/paths/" + Escape(path.Name);
                if (!TryGetObject(livePaths, path.Name, out var livePath))
                {
                    Changes.Add(new OpenApiBreakingChange(OpenApiBreakingChangeKind.RemovedPath, pointer));
                    continue;
                }

                foreach (var operationName in OperationNames)
                {
                    if (!TryGetObject(path.Value, operationName, out var baselineOperation))
                    {
                        continue;
                    }

                    var operationPointer = pointer + "/" + operationName;
                    if (TryGetObject(livePath, operationName, out var liveOperation))
                    {
                        var operation = new OperationSite(path.Name, operationName, pointer, path.Value, livePath);
                        CompareParameters(operation, baselineOperation, liveOperation);
                        CompareRequestBody(operation, baselineOperation, liveOperation);
                        CompareResponses(baselineOperation, liveOperation, operationPointer);
                    }
                    else
                    {
                        Changes.Add(new OpenApiBreakingChange(OpenApiBreakingChangeKind.RemovedOperation, operationPointer));
                    }
                }
            }
        }

        /// <summary>Escapes one reference token for a JSON pointer (RFC 6901).</summary>
        private static string Escape(string token) =>
            token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

        private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>The declared JSON types of a schema, without <see langword="null"/> (nullability is not a type change here).</summary>
        private static HashSet<string> DeclaredTypes(JsonElement schema)
        {
            HashSet<string> types = [];
            if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
            {
                return types;
            }

            if (type.ValueKind == JsonValueKind.String)
            {
                types.Add(type.GetString()!);
            }
            else if (type.ValueKind == JsonValueKind.Array)
            {
                types.UnionWith(type.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!));
            }

            types.Remove("null");
            return types;
        }

        private static HashSet<string> RequiredNames(JsonElement schema) =>
            schema.ValueKind == JsonValueKind.Object
                && schema.TryGetProperty("required", out var required)
                && required.ValueKind == JsonValueKind.Array
                ? [.. required.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)]
                : [];

        /// <summary>
        /// Follows local <c>$ref</c>s (<c>#/components/schemas/X</c>) to the schema they name, moving the
        /// pointer with them so a finding names the component, not the reference site.
        /// </summary>
        private static (JsonElement Schema, string Pointer) Resolve(JsonElement root, JsonElement schema, string pointer)
        {
            for (var hops = 0; hops < 32; hops++)
            {
                if (schema.ValueKind != JsonValueKind.Object
                    || !schema.TryGetProperty("$ref", out var reference)
                    || reference.ValueKind != JsonValueKind.String
                    || reference.GetString() is not { } target
                    || !target.StartsWith("#/", StringComparison.Ordinal)
                    || !TryNavigate(root, target[1..], out var resolved))
                {
                    break;
                }

                (schema, pointer) = (resolved, target[1..]);
            }

            return (schema, pointer);
        }

        private static bool TryNavigate(JsonElement root, string pointer, out JsonElement element)
        {
            element = root;
            foreach (var token in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasRequiredFlag(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.True;

        private static string? ReadString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string FormatTypes(HashSet<string> types) =>
            string.Join("|", types.Order(StringComparer.Ordinal));

        /// <summary>
        /// The parameters that apply to one operation, keyed by location and name, the name
        /// compared case-insensitively: the path item's shared parameters, overridden by the operation's
        /// own with the same key (the OpenAPI rule). Each keeps the pointer of the place it is declared.
        /// </summary>
        private static Dictionary<string, ParameterSite> EffectiveParameters(
            JsonElement root,
            JsonElement pathItem,
            string pathPointer,
            JsonElement operation,
            string operationPointer)
        {
            var parameters = new Dictionary<string, ParameterSite>(StringComparer.OrdinalIgnoreCase);
            Collect(pathItem, pathPointer);
            Collect(operation, operationPointer);
            return parameters;

            void Collect(JsonElement owner, string ownerPointer)
            {
                if (owner.ValueKind != JsonValueKind.Object
                    || !owner.TryGetProperty("parameters", out var list)
                    || list.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                var index = 0;
                foreach (var declared in list.EnumerateArray())
                {
                    var sitePointer = ownerPointer + "/parameters/" + index.ToString(CultureInfo.InvariantCulture);
                    index++;

                    var (parameter, _) = Resolve(root, declared, sitePointer);
                    if (ReadString(parameter, "in") is { } location && ReadString(parameter, "name") is { } name)
                    {
                        parameters[location + "|" + name] = new ParameterSite(location, name, parameter, sitePointer);
                    }
                }
            }
        }

        private void CompareParameters(OperationSite operation, JsonElement baselineOperation, JsonElement liveOperation)
        {
            var baselineParameters = EffectiveParameters(
                baselineRoot, operation.BaselinePathItem, operation.PathPointer, baselineOperation, operation.Pointer);
            var liveParameters = EffectiveParameters(
                liveRoot, operation.LivePathItem, operation.PathPointer, liveOperation, operation.Pointer);

            foreach (var (key, baseline) in baselineParameters)
            {
                if (!liveParameters.TryGetValue(key, out var live))
                {
                    Changes.Add(new OpenApiBreakingChange(OpenApiBreakingChangeKind.RemovedParameter, baseline.Pointer)
                    {
                        Detail = operation.Describe(baseline.Describe()),
                    });
                    continue;
                }

                if (live.IsRequired && !baseline.IsRequired)
                {
                    Changes.Add(new OpenApiBreakingChange(OpenApiBreakingChangeKind.ParameterNewlyRequired, live.Pointer)
                    {
                        Detail = operation.Describe(live.Describe()),
                    });
                }

                CompareParameterSchema(operation, baseline, live);
            }

            foreach (var (key, live) in liveParameters)
            {
                if (live.IsRequired && !baselineParameters.ContainsKey(key))
                {
                    Changes.Add(new OpenApiBreakingChange(OpenApiBreakingChangeKind.AddedRequiredParameter, live.Pointer)
                    {
                        Detail = operation.Describe(live.Describe()),
                    });
                }
            }
        }

        private void CompareParameterSchema(OperationSite operation, ParameterSite baseline, ParameterSite live)
        {
            if (!TryGetObject(baseline.Parameter, "schema", out var baselineSchema)
                || !TryGetObject(live.Parameter, "schema", out var liveSchema))
            {
                return;
            }

            var baselineTypes = DeclaredTypes(Resolve(baselineRoot, baselineSchema, baseline.Pointer).Schema);
            var liveTypes = DeclaredTypes(Resolve(liveRoot, liveSchema, live.Pointer).Schema);
            if (baselineTypes.Count > 0 && liveTypes.Count > 0 && !baselineTypes.SetEquals(liveTypes))
            {
                Changes.Add(new OpenApiBreakingChange(OpenApiBreakingChangeKind.ChangedParameterType, baseline.Pointer + "/schema")
                {
                    Detail = operation.Describe(
                        $"{baseline.Describe()} type {FormatTypes(baselineTypes)} -> {FormatTypes(liveTypes)}"),
                });
                return;
            }

            // Same top-level type: anything deeper (an array's items, an object's properties) is compared
            // like any request schema.
            CompareSchema(baselineSchema, baseline.Pointer + "/schema", liveSchema, live.Pointer + "/schema", Direction.Request);
        }

        private void CompareRequestBody(OperationSite operation, JsonElement baselineOperation, JsonElement liveOperation)
        {
            var pointer = operation.Pointer + "/requestBody";
            var hasBaselineBody = TryGetObject(baselineOperation, "requestBody", out var baselineBody);
            var hasLiveBody = TryGetObject(liveOperation, "requestBody", out var liveBody);
            if (hasBaselineBody)
            {
                baselineBody = Resolve(baselineRoot, baselineBody, pointer).Schema;
            }

            if (hasLiveBody)
            {
                liveBody = Resolve(liveRoot, liveBody, pointer).Schema;
            }

            if (hasBaselineBody && !hasLiveBody)
            {
                Changes.Add(new OpenApiBreakingChange(OpenApiBreakingChangeKind.RemovedRequestBody, pointer)
                {
                    Detail = operation.Describe("request body"),
                });
                return;
            }

            if (!hasBaselineBody)
            {
                if (hasLiveBody && HasRequiredFlag(liveBody))
                {
                    Changes.Add(new OpenApiBreakingChange(OpenApiBreakingChangeKind.AddedRequiredRequestBody, pointer)
                    {
                        Detail = operation.Describe("request body"),
                    });
                }

                return;
            }

            if (TryGetObject(baselineBody, "content", out var baselineContent))
            {
                TryGetObject(liveBody, "content", out var liveContent);
                foreach (var mediaType in baselineContent.EnumerateObject()
                    .Where(mediaType => !TryGetObject(liveContent, mediaType.Name, out _)))
                {
                    Changes.Add(new OpenApiBreakingChange(
                        OpenApiBreakingChangeKind.RemovedRequestMediaType,
                        pointer + "/content/" + Escape(mediaType.Name))
                    {
                        Detail = operation.Describe($"request body media type '{mediaType.Name}'"),
                    });
                }
            }

            CompareContent(baselineBody, liveBody, pointer, Direction.Request);
        }

        private void CompareResponses(JsonElement baseline, JsonElement live, string pointer)
        {
            if (!TryGetObject(baseline, "responses", out var baselineResponses))
            {
                return;
            }

            TryGetObject(live, "responses", out var liveResponses);
            foreach (var response in baselineResponses.EnumerateObject())
            {
                var responsePointer = pointer + "/responses/" + Escape(response.Name);
                if (TryGetObject(liveResponses, response.Name, out var liveResponse))
                {
                    CompareContent(response.Value, liveResponse, responsePointer, Direction.Response);
                }
                else
                {
                    Changes.Add(new OpenApiBreakingChange(OpenApiBreakingChangeKind.RemovedResponseStatusCode, responsePointer));
                }
            }
        }

        private void CompareContent(JsonElement baseline, JsonElement live, string pointer, Direction direction)
        {
            if (!TryGetObject(baseline, "content", out var baselineContent)
                || !TryGetObject(live, "content", out var liveContent))
            {
                return;
            }

            foreach (var mediaType in baselineContent.EnumerateObject())
            {
                if (TryGetObject(mediaType.Value, "schema", out var baselineSchema)
                    && TryGetObject(liveContent, mediaType.Name, out var liveMediaType)
                    && TryGetObject(liveMediaType, "schema", out var liveSchema))
                {
                    var schemaPointer = pointer + "/content/" + Escape(mediaType.Name) + "/schema";
                    CompareSchema(baselineSchema, schemaPointer, liveSchema, schemaPointer, direction);
                }
            }
        }

        private void CompareSchema(
            JsonElement baseline,
            string baselinePointer,
            JsonElement live,
            string livePointer,
            Direction direction)
        {
            (baseline, baselinePointer) = Resolve(baselineRoot, baseline, baselinePointer);
            (live, livePointer) = Resolve(liveRoot, live, livePointer);

            // A schema pair compared once per direction is enough; this also stops recursive schemas.
            if (!_visited.Add($"{direction}|{baselinePointer}|{livePointer}"))
            {
                return;
            }

            var baselineTypes = DeclaredTypes(baseline);
            var liveTypes = DeclaredTypes(live);
            if (baselineTypes.Count > 0 && liveTypes.Count > 0 && !baselineTypes.SetEquals(liveTypes))
            {
                Changes.Add(new OpenApiBreakingChange(OpenApiBreakingChangeKind.ChangedPropertyType, baselinePointer));
                return;
            }

            CompareProperties(baseline, baselinePointer, live, livePointer, direction);

            if (TryGetObject(baseline, "items", out var baselineItems) && TryGetObject(live, "items", out var liveItems))
            {
                CompareSchema(baselineItems, baselinePointer + "/items", liveItems, livePointer + "/items", direction);
            }
        }

        private void CompareProperties(
            JsonElement baseline,
            string baselinePointer,
            JsonElement live,
            string livePointer,
            Direction direction)
        {
            if (TryGetObject(baseline, "properties", out var baselineProperties))
            {
                TryGetObject(live, "properties", out var liveProperties);
                foreach (var property in baselineProperties.EnumerateObject())
                {
                    var propertyPointer = baselinePointer + "/properties/" + Escape(property.Name);
                    if (TryGetObject(liveProperties, property.Name, out var liveProperty))
                    {
                        CompareSchema(
                            property.Value,
                            propertyPointer,
                            liveProperty,
                            livePointer + "/properties/" + Escape(property.Name),
                            direction);
                    }
                    else if (direction == Direction.Response)
                    {
                        Changes.Add(new OpenApiBreakingChange(OpenApiBreakingChangeKind.RemovedResponseProperty, propertyPointer));
                    }
                }
            }

            if (direction == Direction.Request)
            {
                var alreadyRequired = RequiredNames(baseline);
                foreach (var name in RequiredNames(live).Where(name => !alreadyRequired.Contains(name)))
                {
                    Changes.Add(new OpenApiBreakingChange(
                        OpenApiBreakingChangeKind.RequestPropertyNewlyRequired,
                        livePointer + "/properties/" + Escape(name)));
                }
            }
        }

        /// <summary>One operation present on both sides: where it is and how a finding names it.</summary>
        private sealed record OperationSite(
            string Path,
            string Method,
            string PathPointer,
            JsonElement BaselinePathItem,
            JsonElement LivePathItem)
        {
            public string Pointer => PathPointer + "/" + Method;

            /// <summary>Names the path, the method and <paramref name="what"/>, for example <c>GET /Orders: query parameter 'page'</c>.</summary>
            public string Describe(string what) => $"{Method.ToUpperInvariant()} {Path}: {what}";
        }

        /// <summary>One resolved parameter and the pointer of the place it is declared.</summary>
        private sealed record ParameterSite(string In, string Name, JsonElement Parameter, string Pointer)
        {
            /// <summary>A path parameter is always required, whatever its own required flag says.</summary>
            public bool IsRequired =>
                string.Equals(In, "path", StringComparison.OrdinalIgnoreCase) || HasRequiredFlag(Parameter);

            public string Describe() => $"{In} parameter '{Name}'";
        }
    }
}
