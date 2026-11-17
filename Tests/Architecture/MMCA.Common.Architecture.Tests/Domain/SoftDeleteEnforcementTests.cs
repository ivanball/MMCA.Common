using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Domain;

/// <summary>
/// Soft-delete enforcement guard (ADR-005), driven by the shared
/// <see cref="SoftDeleteEnforcementTestsBase"/>: the framework deletes by setting
/// <c>IsDeleted = true</c>, so EF Core's row-erasing members are banned outside the reviewed
/// framework erasure types the base always allows.
/// <para>
/// This is the rule turned back on its own author. Store and ADC subclass the same base and inherit
/// that framework list, because no module of theirs erases a row of its own; running it here is what
/// keeps the list honest, since a NEW framework eraser would otherwise land in the packages and fail
/// downstream instead of in this repo. Common has no erasure type outside that list, so it adds
/// nothing to <c>AllowedHardDeleteTypes</c>.
/// </para>
/// </summary>
public sealed class SoftDeleteEnforcementTests : SoftDeleteEnforcementTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
