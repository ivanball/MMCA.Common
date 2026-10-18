using System.Text.Json.Nodes;
using AwesomeAssertions;
using MMCA.Common.Testing.Conformance;
using Xunit;

namespace MMCA.Common.Testing.Tests.Conformance;

/// <summary>
/// Covers <see cref="OpenApiBreakingChangeDetector"/>, the comparison behind the opt-in baseline gate of
/// <see cref="OpenApiContractTestsBase{TFixture}"/>. Each case mutates one small fixture document: an
/// identical, additive-only or documentation-only change must pass, and each breaking kind must be
/// reported with its kind and the JSON pointer of what broke.
/// </summary>
public sealed class OpenApiBreakingChangeDetectorTests
{
    /// <summary>
    /// A two-path fixture: a list and a create on <c>/Orders</c>, a read and a delete on
    /// <c>/Orders/{id}</c>, one response DTO reached by <c>$ref</c> from three responses, and one request DTO.
    /// </summary>
    private const string Baseline =
        "{"
        + """  "openapi": "3.1.1","""
        + """  "info": { "title": "Fixture | v1", "version": "1.0.0" },"""
        + """  "paths": {"""
        + """    "/Orders": {"""
        + """      "get": {"""
        + """        "summary": "Lists orders.","""
        + """        "responses": {"""
        + """          "200": {"""
        + """            "description": "OK","""
        + """            "content": {"""
        + """              "application/json": {"""
        + """                "schema": { "type": "array", "items": { "$ref": "#/components/schemas/OrderDTO" } }"""
        + "              }"
        + "            }"
        + "          }"
        + "        }"
        + "      },"
        + """      "post": {"""
        + """        "requestBody": {"""
        + """          "content": {"""
        + """            "application/json": { "schema": { "$ref": "#/components/schemas/CreateOrderRequest" } }"""
        + "          }"
        + "        },"
        + """        "responses": {"""
        + """          "201": {"""
        + """            "description": "Created","""
        + """            "content": {"""
        + """              "application/json": { "schema": { "$ref": "#/components/schemas/OrderDTO" } }"""
        + "            }"
        + "          },"
        + """          "400": { "description": "Bad Request" }"""
        + "        }"
        + "      }"
        + "    },"
        + """    "/Orders/{id}": {"""
        + """      "get": {"""
        + """        "responses": {"""
        + """          "200": {"""
        + """            "description": "OK","""
        + """            "content": {"""
        + """              "application/json": { "schema": { "$ref": "#/components/schemas/OrderDTO" } }"""
        + "            }"
        + "          },"
        + """          "404": { "description": "Not Found" }"""
        + "        }"
        + "      },"
        + """      "delete": {"""
        + """        "responses": { "204": { "description": "No Content" } }"""
        + "      }"
        + "    }"
        + "  },"
        + """  "components": {"""
        + """    "schemas": {"""
        + """      "OrderDTO": {"""
        + """        "type": "object","""
        + """        "properties": {"""
        + """          "id": { "type": "integer", "format": "int32" },"""
        + """          "total": { "type": "number", "format": "double" },"""
        + """          "note": { "type": [ "null", "string" ], "description": "Free text." }"""
        + "        }"
        + "      },"
        + """      "CreateOrderRequest": {"""
        + """        "type": "object","""
        + """        "required": [ "customerId" ],"""
        + """        "properties": {"""
        + """          "customerId": { "type": "integer", "format": "int32" },"""
        + """          "note": { "type": [ "null", "string" ] }"""
        + "        }"
        + "      }"
        + "    }"
        + "  }"
        + "}";

    [Fact]
    public void IdenticalDocuments_HaveNoBreakingChanges() =>
        OpenApiBreakingChangeDetector.Detect(Baseline, Baseline).Should().BeEmpty();

    [Fact]
    public void AdditiveOnlyChanges_HaveNoBreakingChanges()
    {
        var live = Mutate(document =>
        {
            var paths = document["paths"]!.AsObject();
            paths["/Customers"] = Parse("""{ "get": { "responses": { "200": { "description": "OK" } } } }""");
            paths["/Orders/{id}"]!["put"] = Parse("""{ "responses": { "204": { "description": "No Content" } } }""");
            paths["/Orders/{id}"]!["get"]!["responses"]!["410"] = Parse("""{ "description": "Gone" }""");

            var schemas = document["components"]!["schemas"]!;
            schemas["OrderDTO"]!["properties"]!["placedOn"] = Parse("""{ "type": "string", "format": "date-time" }""");
            schemas["CreateOrderRequest"]!["properties"]!["coupon"] = Parse("""{ "type": [ "null", "string" ] }""");
        });

        OpenApiBreakingChangeDetector.Detect(Baseline, live).Should().BeEmpty(
            "a new path, operation, status code, response property or optional request property breaks no existing client");
    }

    [Fact]
    public void DocumentationOnlyChanges_HaveNoBreakingChanges()
    {
        var live = Mutate(document =>
        {
            document["info"]!["description"] = "Now documented.";
            document["paths"]!["/Orders"]!["get"]!["summary"] = "Lists every order, newest first.";
            document["paths"]!["/Orders"]!["get"]!["description"] = "Paged.";
            document["paths"]!["/Orders/{id}"]!["get"]!["responses"]!["404"]!["description"] = "No such order";

            var orderProperties = document["components"]!["schemas"]!["OrderDTO"]!["properties"]!;
            orderProperties["note"]!["description"] = "Free text, trimmed.";
            orderProperties["total"]!["example"] = 12.5;
            orderProperties["id"]!["title"] = "Identifier";

            // Property order is not part of the contract.
            var request = document["components"]!["schemas"]!["CreateOrderRequest"]!["properties"]!.AsObject();
            var customerId = request["customerId"]!.DeepClone();
            request.Remove("customerId");
            request["customerId"] = customerId;
        });

        OpenApiBreakingChangeDetector.Detect(Baseline, live).Should().BeEmpty(
            "descriptions, summaries, examples, titles and property order are documentation, not contract");
    }

    [Fact]
    public void RemovedPath_IsReported() =>
        AssertSingleBreakingChange(
            document => document["paths"]!.AsObject().Remove("/Orders/{id}"),
            OpenApiBreakingChangeKind.RemovedPath,
            "/paths/~1Orders~1{id}");

    [Fact]
    public void RemovedOperation_IsReported() =>
        AssertSingleBreakingChange(
            document => document["paths"]!["/Orders/{id}"]!.AsObject().Remove("delete"),
            OpenApiBreakingChangeKind.RemovedOperation,
            "/paths/~1Orders~1{id}/delete");

    [Fact]
    public void RemovedResponseStatusCode_IsReported() =>
        AssertSingleBreakingChange(
            document => document["paths"]!["/Orders/{id}"]!["get"]!["responses"]!.AsObject().Remove("404"),
            OpenApiBreakingChangeKind.RemovedResponseStatusCode,
            "/paths/~1Orders~1{id}/get/responses/404");

    [Fact]
    public void RequestPropertyNewlyRequired_IsReported() =>
        AssertSingleBreakingChange(
            document => document["components"]!["schemas"]!["CreateOrderRequest"]!["required"]!.AsArray().Add("note"),
            OpenApiBreakingChangeKind.RequestPropertyNewlyRequired,
            "/components/schemas/CreateOrderRequest/properties/note");

    [Fact]
    public void RemovedResponseProperty_IsReported() =>
        AssertSingleBreakingChange(
            document => document["components"]!["schemas"]!["OrderDTO"]!["properties"]!.AsObject().Remove("total"),
            OpenApiBreakingChangeKind.RemovedResponseProperty,
            "/components/schemas/OrderDTO/properties/total");

    [Fact]
    public void ChangedPropertyType_IsReported() =>
        AssertSingleBreakingChange(
            document => document["components"]!["schemas"]!["OrderDTO"]!["properties"]!["id"]!["type"] = "string",
            OpenApiBreakingChangeKind.ChangedPropertyType,
            "/components/schemas/OrderDTO/properties/id");

    [Fact]
    public void BreakingChange_RendersAsKindAndPointerOnOneLine() =>
        new OpenApiBreakingChange(OpenApiBreakingChangeKind.RemovedPath, "/paths/~1Orders")
            .ToString().Should().Be("RemovedPath /paths/~1Orders");

    private static void AssertSingleBreakingChange(
        Action<JsonNode> mutation,
        OpenApiBreakingChangeKind expectedKind,
        string expectedPointer)
    {
        var changes = OpenApiBreakingChangeDetector.Detect(Baseline, Mutate(mutation));

        changes.Should().ContainSingle()
            .Which.Should().Be(new OpenApiBreakingChange(expectedKind, expectedPointer));
    }

    private static string Mutate(Action<JsonNode> mutation)
    {
        var document = JsonNode.Parse(Baseline)!;
        mutation(document);
        return document.ToJsonString();
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;
}
