using AwesomeAssertions;
using MMCA.Common.API.Caching;
using MMCA.Common.Testing.Support;
using Xunit;

namespace MMCA.Common.Testing.Tests.Support;

/// <summary>
/// Covers <see cref="EvictOutputCacheAssertions.EvictedTagsOf{TController}"/>: it unions the controller-
/// and action-level attributes across every overload, de-duplicates and orders the tags, and refuses an
/// action name the controller does not declare rather than returning an empty set.
/// </summary>
public sealed class EvictOutputCacheAssertionsTests
{
    [Fact]
    public void EvictedTagsOf_UnionsControllerAndActionTags_DistinctAndOrdered() =>
        EvictOutputCacheAssertions.EvictedTagsOf<SampleController>(nameof(SampleController.Rename))
            .Should().Equal("categories", "products", "reviews");

    [Fact]
    public void EvictedTagsOf_ReturnsControllerTagsOnly_ForAnActionWithoutItsOwn() =>
        EvictOutputCacheAssertions.EvictedTagsOf<SampleController>(nameof(SampleController.Read))
            .Should().Equal("categories");

    [Fact]
    public void EvictedTagsOf_Throws_ForAnUnknownAction()
    {
        var act = () => EvictOutputCacheAssertions.EvictedTagsOf<SampleController>("Missing");

        act.Should().Throw<InvalidOperationException>().WithMessage("*SampleController declares no action named Missing*");
    }

    [EvictOutputCache("categories")]
    private sealed class SampleController
    {
        [EvictOutputCache("reviews", "categories")]
        public void Rename()
        {
            // Only the attributes are read; the body never runs.
        }

        [EvictOutputCache("products")]
        public void Rename(int id) => _ = id;

        public void Read()
        {
            // Only the attributes are read; the body never runs.
        }
    }
}
