namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Strongly typed identifier fitness function (ADR-115). Vacuously satisfied in a repo that has not
/// opted in, which is every repo today: the primitive identifier aliases (ADR-048/ADR-085) remain
/// the default identifier model, and this base exists so that the FIRST wrapper a repo declares is
/// held to the shape the framework's converters, comparer and route binding are written against.
/// </summary>
public abstract class StronglyTypedIdTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void StronglyTypedIds_ShouldBe_ReadonlyRecordStructs() =>
        ArchitectureRules.StronglyTypedIdsAreReadonlyRecordStructs(Map);
}
