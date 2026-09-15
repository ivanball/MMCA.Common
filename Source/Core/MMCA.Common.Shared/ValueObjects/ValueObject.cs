namespace MMCA.Common.Shared.ValueObjects;

/// <summary>
/// Abstract base for value objects. Inheriting from <see langword="record"/> provides
/// structural equality via compiler-generated <see cref="object.Equals(object?)"/>
/// and <see cref="object.GetHashCode()"/> based on all declared properties.
/// </summary>
public abstract record ValueObject;
