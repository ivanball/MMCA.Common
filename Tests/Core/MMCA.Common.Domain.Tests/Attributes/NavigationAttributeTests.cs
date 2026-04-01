using AwesomeAssertions;
using MMCA.Common.Domain.Attributes;

namespace MMCA.Common.Domain.Tests.Attributes;

public sealed class NavigationAttributeTests
{
    // ── IsCollection defaults to false ──
    [Fact]
    public void IsCollection_DefaultsToFalse()
    {
        var attribute = new NavigationAttribute();

        attribute.IsCollection.Should().BeFalse();
    }

    // ── IsCollection can be set to true ──
    [Fact]
    public void IsCollection_CanBeSetToTrue()
    {
        var attribute = new NavigationAttribute { IsCollection = true };

        attribute.IsCollection.Should().BeTrue();
    }

    // ── AttributeUsage targets properties only ──
    [Fact]
    public void AttributeUsage_TargetsProperties()
    {
        var usage = typeof(NavigationAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .OfType<AttributeUsageAttribute>()
            .Single();

        usage.ValidOn.Should().Be(AttributeTargets.Property);
    }

    // ── Not inherited ──
    [Fact]
    public void AttributeUsage_IsNotInherited()
    {
        var usage = typeof(NavigationAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .OfType<AttributeUsageAttribute>()
            .Single();

        usage.Inherited.Should().BeFalse();
    }

    // ── AllowMultiple is false ──
    [Fact]
    public void AttributeUsage_DoesNotAllowMultiple()
    {
        var usage = typeof(NavigationAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .OfType<AttributeUsageAttribute>()
            .Single();

        usage.AllowMultiple.Should().BeFalse();
    }

    // ── Can be read from a property ──
    [Fact]
    public void Attribute_CanBeReadFromProperty()
    {
        var prop = typeof(EntityWithNavigation).GetProperty(nameof(EntityWithNavigation.Children))!;
        var attribute = prop.GetCustomAttributes(typeof(NavigationAttribute), false)
            .OfType<NavigationAttribute>()
            .Single();

        attribute.IsCollection.Should().BeTrue();
    }

    private sealed class EntityWithNavigation
    {
        [Navigation(IsCollection = true)]
        public List<string> Children { get; set; } = [];
    }
}
