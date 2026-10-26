namespace MMCA.Common.Architecture.Tests.SliceFixtures.Stranded;

/// <summary>
/// A query contract one namespace away from <c>FixtureTwoContractHandlerBase</c>, which handles it
/// beside a second stranded contract (L242).
/// </summary>
internal sealed record GetFixtureFarQuery(int Id);
