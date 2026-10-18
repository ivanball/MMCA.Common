using MMCA.Common.Domain.Attributes;

namespace MMCA.Common.Architecture.Tests.PiiInventoryFixtures;

/// <summary>
/// The data subject the personal-data inventory fixtures (<c>pii-inventory-*.md</c> beside this file)
/// describe: two <c>[Pii]</c> members the inventory must cover and one unmarked member it must not
/// require.
/// </summary>
internal sealed class FixturePiiSubject
{
    /// <summary>Gets a non-personal identifier, so no inventory row is required for it.</summary>
    public int Id { get; init; }

    /// <summary>Gets the subject's email address.</summary>
    [Pii]
    public string Email { get; init; } = string.Empty;

    /// <summary>Gets the subject's phone number.</summary>
    [Pii]
    public string Phone { get; init; } = string.Empty;
}
