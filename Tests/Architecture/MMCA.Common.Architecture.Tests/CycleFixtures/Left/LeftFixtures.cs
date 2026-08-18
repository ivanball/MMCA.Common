using MMCA.Common.Architecture.Tests.CycleFixtures.Right;

namespace MMCA.Common.Architecture.Tests.CycleFixtures.Left;

/// <summary>Half of the deliberate namespace cycle: a Left type whose property type lives in Right.</summary>
public sealed class LeftService
{
    /// <summary>The Left -&gt; Right edge the cycle rule must see.</summary>
    public RightModel? Model { get; set; }
}

/// <summary>The base type Right derives from, closing the cycle back into Left.</summary>
public abstract class LeftModelBase
{
    /// <summary>An arbitrary member so the fixture type is not empty.</summary>
    public int Id { get; set; }
}
