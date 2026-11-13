using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Asp.Versioning;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers.Administration;
using MMCA.Common.API.Tests.OpenApi;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Auditing;
using MMCA.Common.Shared.Auth.Permissions;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Administration;

/// <summary>
/// The audit-history controller base: its constructor guards on the audited-type list, the alias
/// lookup by simple type name, the refusals that never reach the reader, the pass-through of the full
/// type name, key and paging, the permission that gates every derived controller, and the documented
/// response shape (a bare array of <see cref="AuditHistoryEntryDTO"/>).
/// </summary>
public sealed class AuditHistoryControllerBaseTests
{
    private readonly Mock<IAuditTrailReader> _reader = new();

    // -- Rule 1: constructor guards --
    [Fact]
    public void Ctor_NoTypes_Throws()
    {
        Action act = () => _ = new TestAuditHistoryController(_reader.Object);

        act.Should().Throw<ArgumentException>("a controller that audits nothing is a configuration mistake");
    }

    [Fact]
    public void Ctor_NullReaderOrArray_Throws()
    {
        Action nullReader = () => _ = new TestAuditHistoryController(null!, typeof(AuditedOrder));
        Action nullArray = () => _ = new TestAuditHistoryController(_reader.Object, null!);

        nullReader.Should().Throw<ArgumentNullException>();
        nullArray.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullEntry_Throws()
    {
        Action act = () => _ = new TestAuditHistoryController(_reader.Object, typeof(AuditedOrder), null!);

        act.Should().Throw<ArgumentException>().Which.Should().NotBeOfType<ArgumentNullException>(
            "a null entry inside the array is an ArgumentException, distinct from a null array");
    }

    [Fact]
    public void Ctor_TypeNotAudited_Throws()
    {
        Action act = () => _ = new TestAuditHistoryController(_reader.Object, typeof(AuditedOrder), typeof(UnauditedOrder));

        act.Should().Throw<ArgumentException>("only an IAuditedEntity has a recorded history to show");
    }

    [Fact]
    public void Ctor_DuplicateSimpleNames_Throw()
    {
        Action act = () => _ = new TestAuditHistoryController(
            _reader.Object, typeof(AuditedOrder), typeof(OtherModule.AuditedOrder));

        act.Should().Throw<ArgumentException>("two types answering to one alias would make the route ambiguous");
    }

    // -- Rule 2: the alias map --
    [Fact]
    public void AuditedEntityTypes_MapsSimpleNameToType_IgnoringCase()
    {
        var sut = new TestAuditHistoryController(_reader.Object, typeof(AuditedOrder), typeof(AuditedInvoice));

        sut.Map.Should().HaveCount(2);
        sut.Map["AuditedOrder"].Should().Be<AuditedOrder>();
        sut.Map["auditedinvoice"].Should().Be<AuditedInvoice>("the alias lookup ignores case");
        sut.ExposedReader.Should().BeSameAs(_reader.Object);
    }

    // -- Rule 3: unknown alias is a 404 and never reads --
    [Fact]
    public async Task UnknownAlias_Returns404_WithCode_ReaderNotCalled()
    {
        var sut = CreateController(typeof(AuditedOrder));

        ActionResult<IReadOnlyList<AuditHistoryEntryDTO>> result = await sut.GetHistoryAsync("Customer", "42", cancellationToken: CancellationToken.None);

        var objectResult = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        JsonSerializer.Serialize(problem.Extensions["errors"]).Should().Contain(AuditHistoryControllerBase.UnknownEntityErrorCode);
        AuditHistoryControllerBase.UnknownEntityErrorCode.Should().Be("AuditHistory.UnknownEntity");
        _reader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AliasIsCaseInsensitive()
    {
        var entries = Entries();
        _reader.Setup(r => r.GetForEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);
        var sut = CreateController(typeof(AuditedOrder));

        ActionResult<IReadOnlyList<AuditHistoryEntryDTO>> result = await sut.GetHistoryAsync("auditedORDER", "42", cancellationToken: CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        _reader.Verify(
            r => r.GetForEntityAsync(typeof(AuditedOrder).FullName!, "42", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // -- Rule 4: blank key is a 400 and never reads --
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankKey_Returns400_ReaderNotCalled(string key)
    {
        var sut = CreateController(typeof(AuditedOrder));

        ActionResult<IReadOnlyList<AuditHistoryEntryDTO>> result = await sut.GetHistoryAsync("AuditedOrder", key, cancellationToken: CancellationToken.None);

        var objectResult = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        JsonSerializer.Serialize(problem.Extensions["errors"]).Should().Contain(AuditHistoryControllerBase.KeyRequiredErrorCode);
        AuditHistoryControllerBase.KeyRequiredErrorCode.Should().Be("AuditHistory.KeyRequired");
        _reader.VerifyNoOtherCalls();
    }

    // -- Rule 5: the read and the answer --
    [Fact]
    public async Task KnownAlias_PassesFullTypeNameKeyPageAndSize()
    {
        using var cts = new CancellationTokenSource();
        _reader.Setup(r => r.GetForEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Entries());
        var sut = CreateController(typeof(AuditedOrder), typeof(AuditedInvoice));

        await sut.GetHistoryAsync("AuditedInvoice", "inv-7", page: 3, pageSize: 25, cancellationToken: cts.Token);

        _reader.Verify(
            r => r.GetForEntityAsync(typeof(AuditedInvoice).FullName!, "inv-7", 3, 25, cts.Token),
            Times.Once);
        _reader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Success_Returns200_BareListInReaderOrder()
    {
        IReadOnlyList<AuditHistoryEntryDTO> entries = Entries();
        _reader.Setup(r => r.GetForEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entries);
        var sut = CreateController(typeof(AuditedOrder));

        ActionResult<IReadOnlyList<AuditHistoryEntryDTO>> result = await sut.GetHistoryAsync("AuditedOrder", "42", cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok.Value.Should().BeSameAs(entries, "the reader's newest-first list is served unchanged, with no envelope");
    }

    // -- Rule 6: paging bounds are model-validated --
    [Fact]
    public void PageAndPageSize_AreRangeValidatedByApiController()
    {
        typeof(AuditHistoryProbeController).GetCustomAttributes<ApiControllerAttribute>(inherit: true)
            .Should().NotBeEmpty("[ApiController] turns a [Range] violation into an automatic 400");

        ParameterInfo[] parameters = typeof(AuditHistoryControllerBase)
            .GetMethod(nameof(AuditHistoryControllerBase.GetHistoryAsync))!
            .GetParameters();

        RangeAttribute page = parameters.Single(p => p.Name == "page").GetCustomAttribute<RangeAttribute>()!;
        page.Minimum.Should().Be(1);
        page.Maximum.Should().Be(int.MaxValue);
        parameters.Single(p => p.Name == "page").DefaultValue.Should().Be(1);

        RangeAttribute pageSize = parameters.Single(p => p.Name == "pageSize").GetCustomAttribute<RangeAttribute>()!;
        pageSize.Minimum.Should().Be(1);
        pageSize.Maximum.Should().Be(500);
        parameters.Single(p => p.Name == "pageSize").DefaultValue.Should().Be(50);
    }

    // -- Rules 7 and 8: the permission gate, and no route on the base --
    [Fact]
    public void ClassCarriesHasPermissionViewAuditTrail()
    {
        var type = typeof(AuditHistoryControllerBase);

        type.GetCustomAttributes<HasPermissionAttribute>().Select(attribute => attribute.Permission)
            .Should().Equal(AdministrationPermissions.ViewAuditTrail);
        AdministrationPermissions.ViewAuditTrail.Should().Be("audit:view");
        type.IsAbstract.Should().BeTrue();
        type.GetCustomAttributes<RouteAttribute>(inherit: true).Should().BeEmpty(
            "each database-per-service host mounts its own derived controller under its own route");
        typeof(AuditHistoryProbeController).GetCustomAttributes<HasPermissionAttribute>(inherit: true)
            .Select(attribute => attribute.Permission)
            .Should().Contain(AdministrationPermissions.ViewAuditTrail, "a derived controller that adds nothing is still gated");
    }

    // -- The documented response shape --
    [Fact]
    public async Task OpenApi_DocumentsArrayOfAuditHistoryEntryDTO()
    {
        await using WebApplication app = await OpenApiProbeHost.CreateAsync(typeof(AuditHistoryProbeController));
        using HttpClient client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;

        root.GetProperty("paths").TryGetProperty("/Admin/AuditHistory/{entity}/{key}", out JsonElement pathItem)
            .Should().BeTrue("the derived controller's route plus the base's entity/key template is described");
        JsonElement responses = pathItem.GetProperty("get").GetProperty("responses");

        JsonElement okContent = responses.GetProperty("200").GetProperty("content");
        okContent.EnumerateObject().Should().Contain(
            media => IsArrayOf(media.Value.GetProperty("schema"), "AuditHistoryEntryDTO"),
            "the 200 body is a bare JSON array of AuditHistoryEntryDTO");

        foreach (string status in new[] { "400", "401", "403", "404" })
        {
            responses.TryGetProperty(status, out _).Should().BeTrue($"the {status} refusal is documented");
        }

        root.GetProperty("components").GetProperty("schemas").TryGetProperty("AuditHistoryEntryDTO", out _)
            .Should().BeTrue();
    }

    // -- Helpers --
    private TestAuditHistoryController CreateController(params Type[] types) =>
        new(_reader.Object, types) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private static List<AuditHistoryEntryDTO> Entries() =>
    [
        new()
        {
            Id = Guid.NewGuid(),
            EntityType = typeof(AuditedOrder).FullName!,
            EntityKey = "42",
            Operation = "Modified",
            PropertyName = "Status",
            ChangedOn = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc),
        },
        new()
        {
            Id = Guid.NewGuid(),
            EntityType = typeof(AuditedOrder).FullName!,
            EntityKey = "42",
            Operation = "Added",
            ChangedOn = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc),
        },
    ];

    private static bool IsArrayOf(JsonElement schema, string itemSchemaName)
    {
        bool isArray = schema.TryGetProperty("type", out JsonElement type)
            && (type.ValueKind == JsonValueKind.String
                ? type.GetString() == "array"
                : type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => t.GetString() == "array"));

        return isArray
            && schema.TryGetProperty("items", out JsonElement items)
            && items.TryGetProperty("$ref", out JsonElement reference)
            && reference.GetString()!.EndsWith("/" + itemSchemaName, StringComparison.Ordinal);
    }

    /// <summary>Exposes the protected members a test needs to observe.</summary>
    private sealed class TestAuditHistoryController(IAuditTrailReader reader, params Type[] auditedEntityTypes)
        : AuditHistoryControllerBase(reader, auditedEntityTypes)
    {
        public IReadOnlyDictionary<string, Type> Map => AuditedEntityTypes;

        public IAuditTrailReader ExposedReader => Reader;
    }
}

/// <summary>A one-line consumer controller, as the design's adoption example shows it.</summary>
[ApiVersion("1.0")]
[Route("Admin/AuditHistory")]
public sealed class AuditHistoryProbeController(IAuditTrailReader reader)
    : AuditHistoryControllerBase(reader, typeof(AuditedOrder), typeof(AuditedInvoice));

/// <summary>An audited entity whose history the probe controllers serve.</summary>
public sealed class AuditedOrder : AuditableBaseEntity<int>, IAuditedEntity;

/// <summary>A second audited entity, so the alias map has more than one entry.</summary>
public sealed class AuditedInvoice : AuditableBaseEntity<int>, IAuditedEntity;

/// <summary>An entity that never opted in to the change trail.</summary>
public sealed class UnauditedOrder : AuditableBaseEntity<int>;

/// <summary>Holds an audited type whose simple name collides with the top-level audited order type.</summary>
public static class OtherModule
{
    /// <summary>Same simple name as the top-level audited order type, different declaring path.</summary>
    public sealed class AuditedOrder : AuditableBaseEntity<int>, IAuditedEntity;
}
