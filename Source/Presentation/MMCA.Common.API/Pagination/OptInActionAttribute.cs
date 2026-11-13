using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace MMCA.Common.API.Pagination;

/// <summary>
/// Makes a base-controller action opt-in: the action removes itself from its controller's application
/// model unless the controller carries an instance of <see cref="OptInAttributeType"/> (declared on the
/// controller or inherited from a base class).
/// </summary>
/// <remarks>
/// A removed action has no route and no API description, so a controller that did not opt in is
/// documented exactly as if the action did not exist. The convention is read off the base action, so
/// an override that does not opt in is removed as well.
/// </remarks>
/// <param name="optInAttributeType">The controller attribute type that opts in to the action.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
internal sealed class OptInActionAttribute(Type optInAttributeType) : Attribute, IActionModelConvention
{
    /// <summary>Gets the controller attribute type that opts in to the action.</summary>
    public Type OptInAttributeType { get; } = optInAttributeType ?? throw new ArgumentNullException(nameof(optInAttributeType));

    /// <summary>Removes the action from its controller unless the controller carries the opt-in attribute.</summary>
    /// <param name="action">The action carrying this convention.</param>
    public void Apply(ActionModel action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var controller = action.Controller;
        if (controller is null || controller.Attributes.Any(OptInAttributeType.IsInstanceOfType))
            return;

        controller.Actions.Remove(action);
    }
}
