using System.Text.Json.Nodes;
using AwesomeAssertions;
using MMCA.Common.Testing.Conformance;
using Xunit;

namespace MMCA.Common.Testing.Tests.Conformance;

/// <summary>
/// Covers the parameter and request-body half of <see cref="OpenApiBreakingChangeDetector"/>. Each case
/// mutates one small fixture document: a removed, newly required, new required or retyped parameter
/// breaks a caller, and so does a removed request body, a new required one, or a removed request media
/// type; an unchanged document, a new optional parameter, a new optional body and a parameter whose name
/// only changed case do not. Every finding names the path, the method and the parameter or body.
/// </summary>
public sealed class OpenApiBreakingChangeDetectorParameterTests
{
    private const string OrderPointer = "/paths/~1Orders~1{id}";

    /// <summary>
    /// One path with a shared path parameter (<c>id</c>), a read with an optional query parameter
    /// (<c>page</c>), a required header (<c>X-Tenant</c>) and a <c>$ref</c>'d query parameter
    /// (<c>pageSize</c>), a replace taking a required JSON-or-XML body, and a delete with no body.
    /// </summary>
    private const string Baseline =
        "{"
        + """  "openapi": "3.1.1","""
        + """  "info": { "title": "Fixture | v1", "version": "1.0.0" },"""
        + """  "paths": {"""
        + """    "/Orders/{id}": {"""
        + """      "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } } ],"""
        + """      "get": {"""
        + """        "parameters": ["""
        + """          { "name": "page", "in": "query", "schema": { "type": "integer" } },"""
        + """          { "name": "X-Tenant", "in": "header", "required": true, "schema": { "type": "string" } },"""
        + """          { "$ref": "#/components/parameters/PageSize" }"""
        + "        ],"
        + """        "responses": { "200": { "description": "OK" } }"""
        + "      },"
        + """      "put": {"""
        + """        "requestBody": {"""
        + """          "required": true,"""
        + """          "content": {"""
        + """            "application/json": { "schema": { "type": "object" } },"""
        + """            "application/xml": { "schema": { "type": "object" } }"""
        + "          }"
        + "        },"
        + """        "responses": { "204": { "description": "No Content" } }"""
        + "      },"
        + """      "delete": {"""
        + """        "responses": { "204": { "description": "No Content" } }"""
        + "      }"
        + "    }"
        + "  },"
        + """  "components": {"""
        + """    "parameters": {"""
        + """      "PageSize": { "name": "pageSize", "in": "query", "schema": { "type": "integer" } }"""
        + "    }"
        + "  }"
        + "}";

    [Fact]
    public void UnchangedParametersAndBodies_HaveNoBreakingChanges() =>
        OpenApiBreakingChangeDetector.Detect(Baseline, Mutate(_ => { })).Should().BeEmpty();

    [Fact]
    public void RemovedParameter_IsReported_NamingThePathMethodAndParameter() =>
        AssertSingleBreakingChange(
            document => GetParameters(document).RemoveAt(0),
            OpenApiBreakingChangeKind.RemovedParameter,
            OrderPointer + "/get/parameters/0",
            "GET /Orders/{id}: query parameter 'page'");

    [Fact]
    public void OptionalParameterBecomingRequired_IsReported() =>
        AssertSingleBreakingChange(
            document => GetParameters(document)[0]!["required"] = true,
            OpenApiBreakingChangeKind.ParameterNewlyRequired,
            OrderPointer + "/get/parameters/0",
            "GET /Orders/{id}: query parameter 'page'");

    [Fact]
    public void NewRequiredParameter_IsReported() =>
        AssertSingleBreakingChange(
            document => GetParameters(document).Add(Parse("""{ "name": "sort", "in": "query", "required": true, "schema": { "type": "string" } }""")),
            OpenApiBreakingChangeKind.AddedRequiredParameter,
            OrderPointer + "/get/parameters/3",
            "GET /Orders/{id}: query parameter 'sort'");

    [Fact]
    public void NewOptionalParameter_IsNotBreaking()
    {
        var live = Mutate(document => GetParameters(document).Add(
            Parse("""{ "name": "sort", "in": "query", "schema": { "type": "string" } }""")));

        OpenApiBreakingChangeDetector.Detect(Baseline, live).Should().BeEmpty(
            "an existing caller that omits a new optional parameter is still a valid call");
    }

    [Fact]
    public void ChangedParameterSchemaType_IsReported() =>
        AssertSingleBreakingChange(
            document => GetParameters(document)[0]!["schema"]!["type"] = "string",
            OpenApiBreakingChangeKind.ChangedParameterType,
            OrderPointer + "/get/parameters/0/schema",
            "GET /Orders/{id}: query parameter 'page' type integer -> string");

    [Fact]
    public void ParameterNamesDifferingOnlyInCase_StayMatched_PathParameterIncluded()
    {
        var live = Mutate(document =>
        {
            GetParameters(document)[0]!["name"] = "Page";
            document["paths"]!["/Orders/{id}"]!["parameters"]![0]!["name"] = "ID";
        });

        OpenApiBreakingChangeDetector.Detect(Baseline, live).Should().BeEmpty(
            "a parameter is identified by (in, name) with the name compared case-insensitively");
    }

    [Fact]
    public void ParameterMovedToAnotherLocation_IsADifferentParameter() =>
        AssertSingleBreakingChange(
            document => GetParameters(document)[0]!["in"] = "header",
            OpenApiBreakingChangeKind.RemovedParameter,
            OrderPointer + "/get/parameters/0",
            "GET /Orders/{id}: query parameter 'page'");

    [Fact]
    public void RemovedPathLevelParameter_IsReportedForEveryOperationItApplied()
    {
        var live = Mutate(document => document["paths"]!["/Orders/{id}"]!.AsObject().Remove("parameters"));

        OpenApiBreakingChangeDetector.Detect(Baseline, live).Select(change => change.Detail).Should().BeEquivalentTo(
            "GET /Orders/{id}: path parameter 'id'",
            "PUT /Orders/{id}: path parameter 'id'",
            "DELETE /Orders/{id}: path parameter 'id'");
    }

    [Fact]
    public void RetypedReferencedParameter_IsResolvedThroughItsRef_AndReportedAtTheOperation() =>
        AssertSingleBreakingChange(
            document => document["components"]!["parameters"]!["PageSize"]!["schema"]!["type"] = "string",
            OpenApiBreakingChangeKind.ChangedParameterType,
            OrderPointer + "/get/parameters/2/schema",
            "GET /Orders/{id}: query parameter 'pageSize' type integer -> string");

    [Fact]
    public void RemovedReferencedParameter_IsReportedByItsResolvedName() =>
        AssertSingleBreakingChange(
            document => GetParameters(document).RemoveAt(2),
            OpenApiBreakingChangeKind.RemovedParameter,
            OrderPointer + "/get/parameters/2",
            "GET /Orders/{id}: query parameter 'pageSize'");

    [Fact]
    public void RemovedRequestBody_IsReported() =>
        AssertSingleBreakingChange(
            document => document["paths"]!["/Orders/{id}"]!["put"]!.AsObject().Remove("requestBody"),
            OpenApiBreakingChangeKind.RemovedRequestBody,
            OrderPointer + "/put/requestBody",
            "PUT /Orders/{id}: request body");

    [Fact]
    public void NewRequiredRequestBody_IsReported() =>
        AssertSingleBreakingChange(
            document => document["paths"]!["/Orders/{id}"]!["delete"]!["requestBody"] = Parse(
                """{ "required": true, "content": { "application/json": { "schema": { "type": "object" } } } }"""),
            OpenApiBreakingChangeKind.AddedRequiredRequestBody,
            OrderPointer + "/delete/requestBody",
            "DELETE /Orders/{id}: request body");

    [Fact]
    public void NewOptionalRequestBody_IsNotBreaking()
    {
        var live = Mutate(document => document["paths"]!["/Orders/{id}"]!["delete"]!["requestBody"] = Parse(
            """{ "content": { "application/json": { "schema": { "type": "object" } } } }"""));

        OpenApiBreakingChangeDetector.Detect(Baseline, live).Should().BeEmpty(
            "an existing caller that sends no body still makes a valid call when the body is optional");
    }

    [Fact]
    public void RemovedRequestMediaType_IsReported() =>
        AssertSingleBreakingChange(
            document => document["paths"]!["/Orders/{id}"]!["put"]!["requestBody"]!["content"]!.AsObject().Remove("application/xml"),
            OpenApiBreakingChangeKind.RemovedRequestMediaType,
            OrderPointer + "/put/requestBody/content/application~1xml",
            "PUT /Orders/{id}: request body media type 'application/xml'");

    [Fact]
    public void ParameterFinding_RendersTheDetailAfterThePointer()
    {
        var change = OpenApiBreakingChangeDetector.Detect(Baseline, Mutate(document => GetParameters(document).RemoveAt(0)))
            .Should().ContainSingle().Subject;

        change.ToString().Should().Be(
            "RemovedParameter /paths/~1Orders~1{id}/get/parameters/0 (GET /Orders/{id}: query parameter 'page')");
    }

    private static void AssertSingleBreakingChange(
        Action<JsonNode> mutation,
        OpenApiBreakingChangeKind expectedKind,
        string expectedPointer,
        string expectedDetail)
    {
        var changes = OpenApiBreakingChangeDetector.Detect(Baseline, Mutate(mutation));

        changes.Should().ContainSingle()
            .Which.Should().Be(new OpenApiBreakingChange(expectedKind, expectedPointer) { Detail = expectedDetail });
    }

    private static JsonArray GetParameters(JsonNode document) =>
        document["paths"]!["/Orders/{id}"]!["get"]!["parameters"]!.AsArray();

    private static string Mutate(Action<JsonNode> mutation)
    {
        var document = JsonNode.Parse(Baseline)!;
        mutation(document);
        return document.ToJsonString();
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;
}
