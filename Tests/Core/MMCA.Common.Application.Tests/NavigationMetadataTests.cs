using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.Tests;

/// <summary>
/// Tests for <see cref="NavigationMetadata"/> verifying supported/unsupported include management.
/// Since the Add methods are internal, we use reflection to invoke them (InternalsVisibleTo
/// is not set for Application.Tests, and NavigationMetadata is a framework class populated
/// by NavigationMetadataProvider in the same assembly).
/// </summary>
public sealed class NavigationMetadataTests
{
    private static readonly MethodInfo AddSupportedMethod =
        typeof(NavigationMetadata).GetMethod("AddSupported", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo AddUnsupportedMethod =
        typeof(NavigationMetadata).GetMethod("AddUnsupported", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo AddSupportedRangeMethod =
        typeof(NavigationMetadata).GetMethod("AddSupportedRange", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo AddUnsupportedRangeMethod =
        typeof(NavigationMetadata).GetMethod("AddUnsupportedRange", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static NavigationPropertyInfo CreateInfo(string name, NavigationType type) =>
        new(name, type, typeof(object), typeof(object));

    // ── Empty state ──
    [Fact]
    public void NewInstance_HasEmptySupportedIncludes()
    {
        var sut = new NavigationMetadata();

        sut.SupportedIncludes.Should().BeEmpty();
    }

    [Fact]
    public void NewInstance_HasEmptyUnsupportedIncludes()
    {
        var sut = new NavigationMetadata();

        sut.UnsupportedIncludes.Should().BeEmpty();
    }

    // ── AddSupported ──
    [Fact]
    public void AddSupported_AddsToSupportedList()
    {
        var sut = new NavigationMetadata();
        var info = CreateInfo("Category", NavigationType.ForeignKey);

        AddSupportedMethod.Invoke(sut, [info]);

        sut.SupportedIncludes.Should().HaveCount(1);
        sut.SupportedIncludes[0].PropertyName.Should().Be("Category");
    }

    // ── AddUnsupported ──
    [Fact]
    public void AddUnsupported_AddsToUnsupportedList()
    {
        var sut = new NavigationMetadata();
        var info = CreateInfo("OrderLines", NavigationType.ChildCollection);

        AddUnsupportedMethod.Invoke(sut, [info]);

        sut.UnsupportedIncludes.Should().HaveCount(1);
        sut.UnsupportedIncludes[0].PropertyName.Should().Be("OrderLines");
    }

    // ── AddSupportedRange ──
    [Fact]
    public void AddSupportedRange_AddsMultipleItems()
    {
        var sut = new NavigationMetadata();
        var items = new[]
        {
            CreateInfo("Category", NavigationType.ForeignKey),
            CreateInfo("Seller", NavigationType.ForeignKey),
        };

        AddSupportedRangeMethod.Invoke(sut, [items.AsEnumerable()]);

        sut.SupportedIncludes.Should().HaveCount(2);
    }

    // ── AddUnsupportedRange ──
    [Fact]
    public void AddUnsupportedRange_AddsMultipleItems()
    {
        var sut = new NavigationMetadata();
        var items = new[]
        {
            CreateInfo("Items", NavigationType.ChildCollection),
            CreateInfo("Tags", NavigationType.ChildCollection),
        };

        AddUnsupportedRangeMethod.Invoke(sut, [items.AsEnumerable()]);

        sut.UnsupportedIncludes.Should().HaveCount(2);
    }

    // ── Mixed ──
    [Fact]
    public void SupportedAndUnsupported_AreSeparateLists()
    {
        var sut = new NavigationMetadata();
        var supported = CreateInfo("Category", NavigationType.ForeignKey);
        var unsupported = CreateInfo("Items", NavigationType.ChildCollection);

        AddSupportedMethod.Invoke(sut, [supported]);
        AddUnsupportedMethod.Invoke(sut, [unsupported]);

        sut.SupportedIncludes.Should().HaveCount(1);
        sut.UnsupportedIncludes.Should().HaveCount(1);
        sut.SupportedIncludes[0].PropertyName.Should().Be("Category");
        sut.UnsupportedIncludes[0].PropertyName.Should().Be("Items");
    }

    // ── NavigationPropertyInfo record ──
    [Fact]
    public void NavigationPropertyInfo_StoresAllProperties()
    {
        var info = new NavigationPropertyInfo(
            "Orders",
            NavigationType.ChildCollection,
            typeof(string),
            typeof(int));

        info.PropertyName.Should().Be("Orders");
        info.Type.Should().Be(NavigationType.ChildCollection);
        info.DeclaringEntityType.Should().Be<string>();
        info.TargetEntityType.Should().Be<int>();
    }

    [Fact]
    public void NavigationPropertyInfo_Equality_Works()
    {
        var info1 = new NavigationPropertyInfo("Name", NavigationType.ForeignKey, typeof(string), typeof(int));
        var info2 = new NavigationPropertyInfo("Name", NavigationType.ForeignKey, typeof(string), typeof(int));

        info1.Should().Be(info2);
    }
}
