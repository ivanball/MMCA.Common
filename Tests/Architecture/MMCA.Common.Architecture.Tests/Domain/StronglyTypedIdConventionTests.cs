using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Domain;

/// <summary>
/// The strongly typed identifier shape rule over the framework's own assemblies, driven by the
/// shared <see cref="StronglyTypedIdTestsBase"/>. It passes vacuously today and is meant to: no
/// production type in <c>Source/</c> declares a wrapper (ADR-115 ships the capability with zero
/// adoption, the way ADR-104 does for smart enumerations), so the rule is here to hold the FIRST one
/// to the shape the converters, comparer and route binding are written against.
/// </summary>
public sealed class StronglyTypedIdConventionTests : StronglyTypedIdTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
}
