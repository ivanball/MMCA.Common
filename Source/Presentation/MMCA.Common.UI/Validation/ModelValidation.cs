using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;

namespace MMCA.Common.UI.Validation;

/// <summary>
/// Bridges a form model's declared rules onto MudBlazor's field <c>Validation</c> parameter, so a page
/// declares its rules once (on the model) instead of scattering <c>Required</c> / <c>MaxLength</c>
/// across the markup and re-checking them by hand.
/// <para>
/// Usage (the model-wide bridge, which is what almost every page wants):
/// </para>
/// <code>
/// &lt;MudForm @ref="_form" Model="_model"&gt;
///     &lt;MudTextField @bind-Value="_model.Title"
///                   For="@(() =&gt; _model.Title)"
///                   Validation="@_validate" /&gt;
/// &lt;/MudForm&gt;
///
/// private readonly MyModel _model = new();
/// private Func&lt;object, string, IEnumerable&lt;string&gt;&gt; _validate = default!;
/// protected override void OnInitialized() =&gt; _validate = ModelValidation.For(_model, new DataAnnotationsModelValidator(L));
/// </code>
/// </summary>
public static class ModelValidation
{
    /// <summary>
    /// Creates the delegate MudBlazor invokes with <c>(model, memberPath)</c> for any field that sets
    /// <c>For</c> inside a <c>MudForm</c> that sets <c>Model</c>. One delegate serves every field on
    /// the form: the path MudBlazor passes selects the rules to run.
    /// </summary>
    /// <param name="model">
    /// The form model. MudBlazor normally passes its own <c>MudForm.Model</c> back into the delegate;
    /// this instance is the fallback used when it does not, so a field still validates outside a form.
    /// </param>
    /// <param name="validator">
    /// The rule engine to run: a <see cref="DataAnnotationsModelValidator"/> built over the page's
    /// localizer, or a consumer implementation (a FluentValidation adapter, for example) that sources
    /// the rules elsewhere.
    /// </param>
    /// <returns>A delegate assignable to a MudBlazor field's <c>Validation</c> parameter.</returns>
    public static Func<object, string, IEnumerable<string>> For(object model, IModelValidator validator)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(validator);

        return (instance, propertyPath) => validator.Validate(instance ?? model, propertyPath);
    }

    /// <summary>
    /// Strongly-typed, single-field bridge: the property is named by expression (so a rename is a
    /// compile error) and the delegate receives the field's own value. Use this for a field that has
    /// no <c>For</c> expression, or that lives outside a <c>MudForm</c> with a <c>Model</c>.
    /// <para>
    /// Rules come from DataAnnotations only, because the value being validated has not necessarily
    /// been written to the model yet. For a model-wide bridge with a pluggable rule engine, use
    /// <see cref="For"/>.
    /// </para>
    /// </summary>
    /// <typeparam name="TModel">The form model type.</typeparam>
    /// <typeparam name="TValue">The property's type, which is also the field's value type.</typeparam>
    /// <param name="model">The form model instance that declares the property.</param>
    /// <param name="property">A property-access expression, for example <c>m =&gt; m.Title</c>.</param>
    /// <param name="validator">
    /// The validator whose localizer resolves each <c>ErrorMessage</c> as a resource key.
    /// </param>
    /// <returns>A delegate assignable to a MudBlazor field's <c>Validation</c> parameter.</returns>
    public static Func<TValue, IEnumerable<string>> ForProperty<TModel, TValue>(
        TModel model,
        Expression<Func<TModel, TValue>> property,
        DataAnnotationsModelValidator validator)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(validator);

        string path = GetPropertyPath(property);
        return value => validator.ValidateValue(model, path, value);
    }

    /// <summary>
    /// Reports whether the model declares the property as required, so a field's <c>Required</c>
    /// parameter (the asterisk and <c>aria-required</c> affordance, not a second rule) can be read off
    /// the same model that supplies the rules. MudBlazor's own required message is not used when a
    /// <c>Validation</c> delegate is present, so the localized message from the model is the one shown.
    /// </summary>
    /// <param name="model">The form model instance.</param>
    /// <param name="propertyPath">Dotted path of the property, relative to <paramref name="model"/>.</param>
    /// <returns><see langword="true"/> when the property carries a <c>[Required]</c> attribute.</returns>
    public static bool IsRequired(object model, string propertyPath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);

        return DataAnnotationsModelValidator.TryResolveOwner(model, propertyPath, out _, out PropertyInfo? property)
            && property.IsDefined(typeof(RequiredAttribute), inherit: true);
    }

    /// <summary>
    /// Renders a property-access expression as the dotted path MudBlazor's <c>For</c> would produce.
    /// </summary>
    /// <typeparam name="TModel">The form model type.</typeparam>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <param name="property">A property-access expression, for example <c>m =&gt; m.Address.City</c>.</param>
    /// <returns>The dotted member path, for example <c>"Address.City"</c>.</returns>
    /// <exception cref="ArgumentException">The expression is not a chain of property accesses.</exception>
    public static string GetPropertyPath<TModel, TValue>(Expression<Func<TModel, TValue>> property)
    {
        ArgumentNullException.ThrowIfNull(property);

        // Unwrap the Convert node the compiler inserts when TValue is a value type boxed to object.
        Expression current = property.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : property.Body;

        var segments = new Stack<string>();
        while (current is MemberExpression member)
        {
            segments.Push(member.Member.Name);
            current = member.Expression!;
        }

        if (segments.Count == 0 || current is not ParameterExpression)
        {
            throw new ArgumentException(
                "The expression must be a chain of property accesses on the model parameter, for example 'm => m.Address.City'.",
                nameof(property));
        }

        return string.Join('.', segments);
    }
}
