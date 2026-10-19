using MMCA.Common.Domain.Interfaces;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests.AnonymizationFixtures;

// Fixtures for the reverse-PII rules in PiiConventionTestsBase. None of them carries [Pii]: the
// personal-data inventory self-test scans this whole assembly for [Pii] members, so a marked fixture
// here would need a row in every pii-inventory-*.md fixture. The marked case is exercised against the
// framework's real RefreshSession instead.

/// <summary>
/// An erasure that overwrites an UNMARKED text property (<see cref="Nickname"/>) and must be reported,
/// alongside state the rule must ignore: a value-type flag the erasure flips, a collection, and a text
/// property the erasure leaves alone.
/// </summary>
internal sealed class UnmarkedOverwriteSubject : IAnonymizable
{
    private readonly List<string> _tags = ["vip"];

    public string? Nickname { get; private set; } = "Ada";

    public string City { get; init; } = "Atlanta";

    public bool IsErased { get; private set; }

    public IReadOnlyList<string> Tags => _tags.AsReadOnly();

    public Result Anonymize()
    {
        Nickname = null;
        IsErased = true;
        return Result.Success();
    }
}

/// <summary>An erasure that fails on its sample, which the rule must report rather than skip.</summary>
internal sealed class FailingAnonymizeSubject : IAnonymizable
{
    public string? Nickname { get; private set; } = "Ada";

    public Result Anonymize() =>
        Result.Failure(Error.Invariant("Fixture.CannotErase", "The fixture refuses erasure.", nameof(Anonymize)));
}

/// <summary>An anonymizable entity the coverage tests leave out of the sample table.</summary>
internal sealed class UnsampledSubject : IAnonymizable
{
    public Result Anonymize() => Result.Success();
}

/// <summary>An abstract anonymizable base: not a concrete entity, so it needs no sample.</summary>
internal abstract class AbstractAnonymizableSubject : IAnonymizable
{
    public abstract Result Anonymize();
}

/// <summary>A plain entity with no erasure path: neither rule has anything to say about it.</summary>
internal sealed class NotAnonymizableSubject
{
    public string? Nickname { get; init; } = "Ada";
}
