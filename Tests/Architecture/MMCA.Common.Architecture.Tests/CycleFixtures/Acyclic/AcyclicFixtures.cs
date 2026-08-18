using MMCA.Common.Architecture.Tests.CycleFixtures.Left;

namespace MMCA.Common.Architecture.Tests.CycleFixtures.Acyclic;

/// <summary>An acyclic neighbour: it points at Left and nothing in Left or Right points back at it.</summary>
public sealed class AcyclicConsumer
{
    /// <summary>The one-way Acyclic -&gt; Left edge.</summary>
    public LeftService? Service { get; set; }
}
