using MMCA.Common.Architecture.Tests.CycleFixtures.Left;

namespace MMCA.Common.Architecture.Tests.CycleFixtures.Right;

/// <summary>Half of the deliberate namespace cycle: a Right type deriving from a Left base.</summary>
public sealed class RightModel : LeftModelBase
{
    /// <summary>An arbitrary member so the fixture type is not empty.</summary>
    public string Name { get; set; } = string.Empty;
}
