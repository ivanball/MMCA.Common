using MMCA.Common.Shared.Identifiers;

namespace MMCA.Common.Shared.Tests.Identifiers;

/// <summary>
/// The canonical two-line declaration, once per supported primitive. These are the only strongly
/// typed identifiers anywhere in the repository: no production type in <c>Source/</c> adopts one, by
/// design (ADR-115), so the contract is pinned by fixtures exactly as <c>Enumeration&lt;T&gt;</c>'s is.
/// </summary>
public readonly record struct OrderId(int Value) : IStronglyTypedId<OrderId, int>
{
    /// <summary>Wraps a primitive order key.</summary>
    public static OrderId From(int value) => new(value);
}

/// <summary>A second int-backed identifier, so a test can prove the two do not interchange.</summary>
public readonly record struct CustomerId(int Value) : IStronglyTypedId<CustomerId, int>
{
    /// <summary>Wraps a primitive customer key.</summary>
    public static CustomerId From(int value) => new(value);
}

/// <summary>A long-backed identifier.</summary>
public readonly record struct LineId(long Value) : IStronglyTypedId<LineId, long>
{
    /// <summary>Wraps a primitive line key.</summary>
    public static LineId From(long value) => new(value);
}

/// <summary>A Guid-backed identifier, the shape an externally-issued key takes.</summary>
public readonly record struct SpeakerId(Guid Value) : IStronglyTypedId<SpeakerId, Guid>
{
    /// <summary>Wraps a speaker key.</summary>
    public static SpeakerId From(Guid value) => new(value);
}

/// <summary>A string-backed identifier, the shape a natural key takes.</summary>
public readonly record struct SkuId(string Value) : IStronglyTypedId<SkuId, string>
{
    /// <summary>Wraps a stock-keeping unit.</summary>
    public static SkuId From(string value) => new(value);
}
