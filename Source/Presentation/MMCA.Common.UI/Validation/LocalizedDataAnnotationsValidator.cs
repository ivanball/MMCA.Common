using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Resources;

namespace MMCA.Common.UI.Validation;

/// <summary>
/// Drop-in replacement for <c>&lt;DataAnnotationsValidator /&gt;</c> inside an <c>EditForm</c> that
/// resolves every <c>ErrorMessage</c> as a resource key (ADR-027). It runs the same DataAnnotations
/// rules through <see cref="DataAnnotationsModelValidator"/>, the localizing path the MudForm pages
/// already use, and writes the results into the form's <see cref="EditContext"/>, so fields bound
/// with <c>For</c> show the translated message. A message that is not a known key passes through
/// unchanged, exactly as with the stock validator.
/// <para>
/// A field is re-validated when it changes, and every public property of the model is validated
/// when the form is submitted.
/// </para>
/// </summary>
public sealed class LocalizedDataAnnotationsValidator : ComponentBase, IDisposable
{
    private EditContext? _editContext;
    private ValidationMessageStore? _messages;
    private DataAnnotationsModelValidator? _validator;

    [CascadingParameter]
    private EditContext? CurrentEditContext { get; set; }

    [Inject]
    private IStringLocalizer<SharedResource> Localizer { get; set; } = default!;

    /// <inheritdoc />
    public void Dispose() => Detach();

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (CurrentEditContext is null)
        {
            throw new InvalidOperationException(
                $"{nameof(LocalizedDataAnnotationsValidator)} requires a cascading {nameof(EditContext)}. Place it inside an EditForm.");
        }

        if (ReferenceEquals(CurrentEditContext, _editContext))
        {
            return;
        }

        Detach();

        _editContext = CurrentEditContext;
        _messages = new ValidationMessageStore(_editContext);
        _validator = new DataAnnotationsModelValidator(Localizer);
        _editContext.OnFieldChanged += HandleFieldChanged;
        _editContext.OnValidationRequested += HandleValidationRequested;
    }

    private void HandleFieldChanged(object? sender, FieldChangedEventArgs e)
    {
        if (_editContext is null || _messages is null || _validator is null)
        {
            return;
        }

        FieldIdentifier field = e.FieldIdentifier;
        _messages.Clear(field);
        _messages.Add(field, _validator.Validate(field.Model, field.FieldName));
        _editContext.NotifyValidationStateChanged();
    }

    private void HandleValidationRequested(object? sender, ValidationRequestedEventArgs e)
    {
        if (_editContext is null || _messages is null || _validator is null)
        {
            return;
        }

        _messages.Clear();

        object model = _editContext.Model;
        IEnumerable<string> propertyNames = model.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Select(property => property.Name)
            .Distinct(StringComparer.Ordinal);

        foreach (string propertyName in propertyNames)
        {
            _messages.Add(new FieldIdentifier(model, propertyName), _validator.Validate(model, propertyName));
        }

        _editContext.NotifyValidationStateChanged();
    }

    private void Detach()
    {
        if (_editContext is null)
        {
            return;
        }

        _editContext.OnFieldChanged -= HandleFieldChanged;
        _editContext.OnValidationRequested -= HandleValidationRequested;
        _messages?.Clear();
        _editContext = null;
        _messages = null;
        _validator = null;
    }
}
