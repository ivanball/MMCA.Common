using System.Text.Json;

namespace MMCA.Common.Testing.Conformance;

/// <summary>
/// Compares a committed baseline OpenAPI document with the live one and reports only the changes that
/// break an existing client: a removed path or operation, a removed response status code, a request
/// property newly required, a removed response property, or a changed property type. Everything else
/// passes, additive changes included.
/// </summary>
/// <remarks>
/// <para>
/// Both documents are normalized by reading them structurally rather than textually: paths, operations,
/// status codes and properties are matched by name, so property order never shows up as a change, and
/// only the contract-bearing members are read
/// (<c>paths, responses, requestBody, content, schema, $ref, type, properties, required, items</c>),
/// so descriptions, summaries, examples, titles, tags and every other documentation-only field are
/// ignored by construction.
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
                        CompareOperation(baselineOperation, liveOperation, operationPointer);
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

        private void CompareOperation(JsonElement baseline, JsonElement live, string pointer)
        {
            if (TryGetObject(baseline, "requestBody", out var baselineBody)
                && TryGetObject(live, "requestBody", out var liveBody))
            {
                CompareContent(baselineBody, liveBody, pointer + "/requestBody", Direction.Request);
            }

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
    }
}
