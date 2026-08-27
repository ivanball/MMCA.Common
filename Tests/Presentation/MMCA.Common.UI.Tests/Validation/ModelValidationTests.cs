using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Validation;

namespace MMCA.Common.UI.Tests.Validation;

/// <summary>
/// Unit tests for the MudForm validation bridge: a form field's <c>Validation</c> delegate runs the
/// rules the model already declares, so <c>Required</c> / <c>MaxLength</c> never has to be repeated
/// in markup.
/// </summary>
public sealed class ModelValidationTests
{
    private const string TooLong = "0123456789";

    [Fact]
    public void For_ReturnsNoErrors_WhenThePropertyIsValid()
    {
        var model = new SampleModel { Name = "Ada" };

        var validate = ModelValidation.For(model);

        validate(model, nameof(SampleModel.Name)).Should().BeEmpty();
    }

    [Fact]
    public void For_ReturnsTheRequiredMessage_WhenThePropertyIsEmpty()
    {
        var model = new SampleModel { Name = string.Empty };

        var validate = ModelValidation.For(model);

        validate(model, nameof(SampleModel.Name)).Should().ContainSingle()
            .Which.Should().Be("Name is required");
    }

    [Fact]
    public void For_ReturnsTheMaxLengthMessage_WhenThePropertyIsTooLong()
    {
        var model = new SampleModel { Name = TooLong };

        var validate = ModelValidation.For(model);

        validate(model, nameof(SampleModel.Name)).Should().ContainSingle()
            .Which.Should().Be("Name is too long");
    }

    [Fact]
    public void For_ValidatesOnlyTheRequestedProperty()
    {
        // Every field on a form shares one delegate; the path MudBlazor passes selects the rules.
        var model = new SampleModel { Name = string.Empty, Email = "not-an-email" };

        var validate = ModelValidation.For(model);

        validate(model, nameof(SampleModel.Email)).Should().ContainSingle()
            .Which.Should().Be("Email is invalid");
    }

    [Fact]
    public void For_WalksADottedPath_ToANestedProperty()
    {
        var model = new SampleModel { Name = "Ada", Age = 36, Child = new ChildModel { City = string.Empty } };

        var validate = ModelValidation.For(model);

        validate(model, "Child.City").Should().ContainSingle()
            .Which.Should().Be("City is required");
    }

    [Fact]
    public void For_ReturnsNoErrors_ForAnUnreachableOrUnknownPath()
    {
        // A null link in the chain, or a member the model does not declare, carries no rules: a
        // partially-built model must not throw mid-keystroke.
        var model = new SampleModel { Name = "Ada" };

        var validate = ModelValidation.For(model);

        validate(model, "Child.City").Should().BeEmpty();
        validate(model, "NoSuchProperty").Should().BeEmpty();
    }

    [Fact]
    public void For_FallsBackToTheCapturedModel_WhenNoInstanceIsPassed()
    {
        var model = new SampleModel { Name = string.Empty };

        var validate = ModelValidation.For(model);

        validate(null!, nameof(SampleModel.Name)).Should().ContainSingle();
    }

    [Fact]
    public void For_RunsAPluggedInValidator_InsteadOfDataAnnotations()
    {
        // The extension point a consumer uses to source rules from FluentValidation without
        // MMCA.Common.UI referencing it.
        var model = new SampleModel { Name = "Ada" };

        var validate = ModelValidation.For(model, new AlwaysFailsValidator());

        validate(model, nameof(SampleModel.Name)).Should().ContainSingle()
            .Which.Should().Be("plugged in: Name");
    }

    [Fact]
    public void DataAnnotationsValidator_ResolvesErrorMessagesAsResourceKeys_WhenLocalized()
    {
        var model = new KeyedModel { Title = string.Empty };
        var localizer = new StubLocalizer(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Some.Required.Key"] = "El titulo es obligatorio.",
        });

        var validate = ModelValidation.For(model, new DataAnnotationsModelValidator(localizer));

        validate(model, nameof(KeyedModel.Title)).Should().ContainSingle()
            .Which.Should().Be("El titulo es obligatorio.");
    }

    [Fact]
    public void DataAnnotationsValidator_PassesAMessageThrough_WhenItIsNotAKnownResourceKey()
    {
        var model = new SampleModel { Name = string.Empty };
        var localizer = new StubLocalizer(new Dictionary<string, string>(StringComparer.Ordinal));

        var validate = ModelValidation.For(model, new DataAnnotationsModelValidator(localizer));

        validate(model, nameof(SampleModel.Name)).Should().ContainSingle()
            .Which.Should().Be("Name is required");
    }

    [Fact]
    public void ForProperty_ValidatesTheIncomingValue_NotTheModelState()
    {
        // The strongly-typed bridge is for a field with no `For`: it must judge the value the field
        // hands it, without depending on whether @bind-Value has written it back yet.
        var model = new SampleModel { Name = "Ada" };

        var validate = ModelValidation.ForProperty(model, m => m.Name);

        validate(TooLong).Should().ContainSingle().Which.Should().Be("Name is too long");
        validate("Grace").Should().BeEmpty();
    }

    [Fact]
    public void IsRequired_ReadsTheRequiredMarkerOffTheModel()
    {
        // Lets a field's Required parameter (asterisk + aria-required) come from the same model that
        // supplies the rules, instead of being asserted a second time in markup.
        var model = new SampleModel { Name = "Ada", Age = 36, Child = new ChildModel() };

        ModelValidation.IsRequired(model, nameof(SampleModel.Name)).Should().BeTrue();
        ModelValidation.IsRequired(model, nameof(SampleModel.Email)).Should().BeFalse();
        ModelValidation.IsRequired(model, "Child.City").Should().BeTrue();
        ModelValidation.IsRequired(model, "NoSuchProperty").Should().BeFalse();
    }

    [Fact]
    public void GetPropertyPath_RendersTheDottedPathMudBlazorWouldProduce()
    {
        ModelValidation.GetPropertyPath((SampleModel m) => m.Name).Should().Be("Name");
        ModelValidation.GetPropertyPath((SampleModel m) => m.Child!.City).Should().Be("Child.City");
        // Boxed to object, so the compiler inserts the Convert node the helper has to unwrap.
        ModelValidation.GetPropertyPath((SampleModel m) => (object)m.Age).Should().Be("Age");
    }

    [Fact]
    public void GetPropertyPath_RejectsAnExpressionThatIsNotAPropertyChain()
    {
        var act = () => ModelValidation.GetPropertyPath((SampleModel m) => m.Name.Length + 1);

        act.Should().Throw<ArgumentException>();
    }

    private sealed class SampleModel
    {
        [Required(ErrorMessage = "Name is required")]
        [MaxLength(5, ErrorMessage = "Name is too long")]
        public string Name { get; set; } = string.Empty;

        [EmailAddress(ErrorMessage = "Email is invalid")]
        public string? Email { get; set; }

        public int Age { get; set; }

        public ChildModel? Child { get; set; }
    }

    private sealed class ChildModel
    {
        [Required(ErrorMessage = "City is required")]
        public string City { get; set; } = string.Empty;
    }

    private sealed class KeyedModel
    {
        [Required(ErrorMessage = "Some.Required.Key")]
        public string Title { get; set; } = string.Empty;
    }

    private sealed class AlwaysFailsValidator : IModelValidator
    {
        public IEnumerable<string> Validate(object model, string propertyPath) =>
            [$"plugged in: {propertyPath}"];
    }

    private sealed class StubLocalizer(Dictionary<string, string> entries) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            entries.TryGetValue(name, out string? value)
                ? new LocalizedString(name, value, resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
