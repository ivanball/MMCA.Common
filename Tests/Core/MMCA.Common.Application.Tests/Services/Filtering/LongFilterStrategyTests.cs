using MMCA.Common.Application.Tests.Services.Filtering.Numeric;

namespace MMCA.Common.Application.Tests.Services.Filtering;

public sealed class LongFilterStrategyTests : NumericFilterStrategyTestsBase<long>
{
    protected override IReadOnlyList<long> Values { get; } =
        [5_000_000_000L, 10_000_000_000L, 15_000_000_000L, 20_000_000_000L];
}
