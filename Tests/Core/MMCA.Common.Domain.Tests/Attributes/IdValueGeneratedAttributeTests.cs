using AwesomeAssertions;
using MMCA.Common.Domain.Attributes;

namespace MMCA.Common.Domain.Tests.Attributes;

public sealed class IdValueGeneratedAttributeTests
{
    // ── AttributeUsage targets classes only ──
    [Fact]
    public void AttributeUsage_TargetsClasses()
    {
        var usage = typeof(IdValueGeneratedAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .OfType<AttributeUsageAttribute>()
            .Single();

        usage.ValidOn.Should().Be(AttributeTargets.Class);
    }

    // ── Not inherited ──
    [Fact]
    public void AttributeUsage_IsNotInherited()
    {
        var usage = typeof(IdValueGeneratedAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .OfType<AttributeUsageAttribute>()
            .Single();

        usage.Inherited.Should().BeFalse();
    }

    // ── AllowMultiple is false ──
    [Fact]
    public void AttributeUsage_DoesNotAllowMultiple()
    {
        var usage = typeof(IdValueGeneratedAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .OfType<AttributeUsageAttribute>()
            .Single();

        usage.AllowMultiple.Should().BeFalse();
    }

    // ── Can be instantiated ──
    [Fact]
    public void Attribute_CanBeInstantiated()
    {
        var attribute = new IdValueGeneratedAttribute();

        attribute.Should().NotBeNull();
    }

    // ── Can be read from a decorated class ──
    [Fact]
    public void Attribute_CanBeReadFromDecoratedClass()
    {
        var attributes = typeof(DecoratedEntity)
            .GetCustomAttributes(typeof(IdValueGeneratedAttribute), false);

        attributes.Should().ContainSingle();
    }

    [Fact]
    public void Attribute_NotPresentOnUndecorated()
    {
        var attributes = typeof(UndecoratedEntity)
            .GetCustomAttributes(typeof(IdValueGeneratedAttribute), false);

        attributes.Should().BeEmpty();
    }

    [IdValueGenerated]
    private sealed class DecoratedEntity;

    private sealed class UndecoratedEntity;
}
