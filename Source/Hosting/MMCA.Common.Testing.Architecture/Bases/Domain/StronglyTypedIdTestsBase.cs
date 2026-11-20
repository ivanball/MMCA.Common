namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Strongly typed identifier fitness function (ADR-115). Vacuously satisfied in a repo that has not
/// opted in (MMCA.ADC and MMCA.Store; MMCA.Helpdesk declares <c>TicketCommentId</c>): the primitive
/// identifier aliases (ADR-048/ADR-085) remain the default identifier model, and this base holds
/// every wrapper a repo declares to the shape the framework's converters, comparer and route binding
/// are written against.
/// </summary>
public abstract class StronglyTypedIdTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void StronglyTypedIds_ShouldBe_ReadonlyRecordStructs() =>
        ArchitectureRules.StronglyTypedIdsAreReadonlyRecordStructs(Map);
}
