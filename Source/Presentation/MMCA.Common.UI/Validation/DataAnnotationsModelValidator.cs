using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Localization;

namespace MMCA.Common.UI.Validation;

/// <summary>
/// <see cref="IModelValidator"/> backed by <c>System.ComponentModel.DataAnnotations</c>. It validates a
/// single property against the attributes declared on the model, so the rules a shared request/form
/// model already carries are the only place those rules are written: markup stops repeating
/// <c>Required</c> / <c>MaxLength</c> per field.
/// <para>
/// Every produced message is looked up as a resource key against the required
/// <see cref="IStringLocalizer"/> (ADR-027). A model therefore declares
/// <c>ErrorMessage = "Some.Resource.Key"</c> and the key is resolved against the host's resources; a
/// message that is not a known key is passed through unchanged, so plain-English
/// <c>ErrorMessage</c> models render exactly as written.
/// </para>
/// </summary>
public sealed class DataAnnotationsModelValidator : IModelValidator
{
    private const BindingFlags PropertyLookup =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

    private readonly IStringLocalizer _localizer;

    /// <summary>
    /// Creates a validator that resolves each error message through <paramref name="localizer"/>.
    /// </summary>
    /// <param name="localizer">
    /// Localizer used to treat every <c>ErrorMessage</c> as a resource key. Pass the page's own
    /// <c>IStringLocalizer&lt;TResource&gt;</c>, so a message that is not one of its keys falls
    /// through unchanged.
    /// </param>
    public DataAnnotationsModelValidator(IStringLocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        _localizer = localizer;
    }

    /// <inheritdoc />
    public IEnumerable<string> Validate(object model, string propertyPath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);

        if (!TryResolveOwner(model, propertyPath, out object? owner, out PropertyInfo? property))
        {
            return [];
        }

        return ValidateResolved(owner, property, property.GetValue(owner));
    }

    /// <summary>
    /// Validates a candidate <paramref name="value"/> against the attributes declared for
    /// <paramref name="propertyPath"/>, instead of the value the model currently holds. Use this when
    /// the incoming value has not been written to the model yet.
    /// </summary>
    /// <param name="model">The form model instance that declares the property.</param>
    /// <param name="propertyPath">Dotted path of the property, relative to <paramref name="model"/>.</param>
    /// <param name="value">The candidate value to validate.</param>
    /// <returns>The error messages for that value, or an empty sequence when it is valid.</returns>
    public IEnumerable<string> ValidateValue(object model, string propertyPath, object? value)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);

        return !TryResolveOwner(model, propertyPath, out object? owner, out PropertyInfo? property)
            ? []
            : ValidateResolved(owner, property, value);
    }

    /// <summary>
    /// Walks a dotted path down to the object that declares the final property. Returns <see langword="false"/>
    /// when a link in the chain is null or the model does not declare the member: an unreachable path
    /// carries no rules, so it cannot fail, and a partially-built model never throws mid-keystroke.
    /// </summary>
    internal static bool TryResolveOwner(
        object model,
        string propertyPath,
        [NotNullWhen(true)] out object? owner,
        [NotNullWhen(true)] out PropertyInfo? property)
    {
        owner = model;
        property = null;

        string[] segments = propertyPath.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < segments.Length; i++)
        {
            PropertyInfo? current = FindProperty(owner.GetType(), segments[i]);
            if (current is null)
            {
                return false;
            }

            if (i == segments.Length - 1)
            {
                property = current;
                return true;
            }

            object? next = current.GetValue(owner);
            if (next is null)
            {
                return false;
            }

            owner = next;
        }

        return false;
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        try
        {
            return type.GetProperty(name, PropertyLookup);
        }
        catch (AmbiguousMatchException)
        {
            // A `new`-shadowed property: the most-derived declaration is the one bound in markup.
            return type.GetProperty(name, PropertyLookup | BindingFlags.DeclaredOnly);
        }
    }

    private List<string> ValidateResolved(object owner, PropertyInfo property, object? value)
    {
        var context = new ValidationContext(owner) { MemberName = property.Name };
        var results = new List<ValidationResult>();
        Validator.TryValidateProperty(value, context, results);

        return [.. results
            .Select(result => Localize(result.ErrorMessage))
            .Where(message => message.Length > 0)];
    }

    private string Localize(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        LocalizedString localized = _localizer[message];
        return localized.ResourceNotFound ? message : localized.Value;
    }
}
