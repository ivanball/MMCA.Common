using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;

namespace MMCA.Common.UI.Tests.Common;

/// <summary>
/// Every optional <see cref="IUIModule"/> slot has a default, so a module that contributes nothing to
/// a slot never has to implement its member: the component-list slots default to empty and the 404
/// content slot to null (the framework's own page).
/// </summary>
public sealed class UIModuleDefaultsTests
{
    [Theory]
    [InlineData(nameof(IUIModule.PublicAppBarComponentTypes), "a module that contributes no public app bar control must not have to implement the member")]
    [InlineData(nameof(IUIModule.FooterComponentTypes), "a module that contributes no footer content must not have to implement the member")]
    [InlineData(nameof(IUIModule.ContentHeaderComponentTypes), "a module that contributes no content header must not have to implement the member")]
    [InlineData(nameof(IUIModule.NotFoundContentType), "a module that supplies no 404 keeps the framework's")]
    public void IUIModule_OptionalSlot_HasAnInertDefault(string member, string reason)
    {
        var property = typeof(IUIModule).GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
        property.Should().NotBeNull($"IUIModule needs a {member} slot");

        object? value = property!.GetValue(new BareModule());

        if (property.PropertyType == typeof(Type))
        {
            value.Should().BeNull(reason);
        }
        else
        {
            property.PropertyType.Should().Be<IReadOnlyList<Type>>();
            ((IEnumerable<Type>)value!).Should().BeEmpty(reason);
        }
    }

    /// <summary>A module that contributes nothing beyond the required members.</summary>
    private sealed class BareModule : IUIModule
    {
        public IReadOnlyList<NavItem> NavItems { get; } = [];

        public Assembly Assembly => typeof(BareModule).Assembly;
    }
}
