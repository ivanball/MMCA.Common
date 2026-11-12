using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Shared.Auditing;

namespace MMCA.Common.Application.Tests.Auditing;

/// <summary>
/// Contract coverage for the audit trail reader's published signature: the paging defaults (a caller
/// that asks for an entity's history without paging must get the first page, not the whole table) and
/// the Shared DTO it returns, which the audit-history endpoint serves unchanged.
/// </summary>
public sealed class AuditTrailReaderContractTests
{
    // DESIGN-QUESTION: the paging-defaults test lived in AuditTrailEntryDTOTests, which item 12 moves to the Shared tests project; that project references Shared only and cannot see IAuditTrailReader, so the test moved here (reading: keep it in Application) rather than being dropped.
    [Fact]
    public void IAuditTrailReader_PagingArguments_DefaultToTheFirstPage()
    {
        var parameters = typeof(IAuditTrailReader)
            .GetMethod(nameof(IAuditTrailReader.GetForEntityAsync))!
            .GetParameters();

        parameters.Single(p => p.Name == "page").DefaultValue.Should().Be(1);
        parameters.Single(p => p.Name == "pageSize").DefaultValue.Should().Be(50);
    }

    [Fact]
    public void IAuditTrailReader_ReturnsTheSharedAuditHistoryEntryDTO()
    {
        var returnType = typeof(IAuditTrailReader)
            .GetMethod(nameof(IAuditTrailReader.GetForEntityAsync))!
            .ReturnType;

        returnType.Should().Be<Task<IReadOnlyList<AuditHistoryEntryDTO>>>(
            "one DTO type travels end to end, from the reader through the endpoint to the UI panel");
    }
}
