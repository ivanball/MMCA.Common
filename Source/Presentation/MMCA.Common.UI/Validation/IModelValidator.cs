namespace MMCA.Common.UI.Validation;

/// <summary>
/// Validates a single property of a form model and returns that property's error messages.
/// <para>
/// This is the pluggable extension point behind <see cref="ModelValidation.For"/>: MudBlazor hands a
/// field validator the form model plus the full path of the member being edited, which is exactly the
/// shape a rule engine needs. <see cref="DataAnnotationsModelValidator"/> is the in-box implementation;
/// a consumer that keeps its rules in FluentValidation supplies its own implementation instead, so
/// MMCA.Common.UI never has to reference a validation library.
/// </para>
/// </summary>
public interface IModelValidator
{
    /// <summary>
    /// Validates one property of <paramref name="model"/>.
    /// </summary>
    /// <param name="model">The form model instance being edited.</param>
    /// <param name="propertyPath">
    /// Dotted path of the property to validate, relative to <paramref name="model"/> (for example
    /// <c>"Title"</c> or <c>"Address.City"</c>). This is what MudBlazor derives from a field's
    /// <c>For</c> expression.
    /// </param>
    /// <returns>
    /// The error messages for that property, or an empty sequence when it is valid. Never <see langword="null"/>.
    /// </returns>
    IEnumerable<string> Validate(object model, string propertyPath);
}
