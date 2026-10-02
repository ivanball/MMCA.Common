using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Microsoft.Extensions.Localization;
using MMCA.Common.UI.Validation;

namespace MMCA.Common.UI.Tests.Validation;

/// <summary>
/// Unit tests for <see cref="OptionalEmailAttribute"/>: a blank value passes (the field is optional),
/// and a non-blank value gets the verdict the server's <c>EmailRules</c> gives it. The server rule is
/// FluentValidation's default <c>EmailAddress()</c> check: exactly one <c>@</c>, neither the first
/// nor the last character. The accepted and rejected values below are the client half of a parity
/// corpus whose server half is <c>EmailRules_FormatVerdict_MatchesTheClientParityCorpus</c> in
/// CommonValidationRulesTests; the two lists must stay identical.
/// </summary>
public sealed class OptionalEmailAttributeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Accepts_BlankValues_BecauseTheFieldIsOptional(string? email) =>
        Validate(email).Should().BeEmpty();

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    [InlineData("a@b")]
    public void Accepts_WhatTheServerAccepts(string email) =>
        Validate(email).Should().BeEmpty();

    [Theory]
    [InlineData("plainaddress")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@@example.com")]
    [InlineData("a@b@c")]
    [InlineData("@")]
    public void Rejects_WhatTheServerRejects(string email) =>
        Validate(email).Should().ContainSingle();

    [Fact]
    public void ErrorMessage_IsEmittedUnchanged_SoItCanBeALocalizationResourceKey()
    {
        var model = new EmailModel { Email = "not-an-email" };
        var validator = new DataAnnotationsModelValidator(
            new StubLocalizer(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Auth.Field.Email.Invalid"] = "Enter a valid email address",
            }));

        validator.Validate(model, nameof(EmailModel.Email)).Should().ContainSingle()
            .Which.Should().Be("Enter a valid email address");
    }

    [Fact]
    public void ErrorMessage_FallsThroughUnchanged_WhenItIsNotAKnownResourceKey()
    {
        var model = new EmailModel { Email = "not-an-email" };
        var validator = new DataAnnotationsModelValidator(
            new StubLocalizer(new Dictionary<string, string>(StringComparer.Ordinal)));

        validator.Validate(model, nameof(EmailModel.Email)).Should().ContainSingle()
            .Which.Should().Be("Auth.Field.Email.Invalid");
    }

    [Fact]
    public void ReportsTheOffendingMember_SoTheMessageLandsOnTheRightField()
    {
        var model = new EmailModel { Email = "user@" };
        var context = new ValidationContext(model) { MemberName = nameof(EmailModel.Email) };
        var results = new List<ValidationResult>();

        Validator.TryValidateProperty(model.Email, context, results);

        results.Should().ContainSingle()
            .Which.MemberNames.Should().ContainSingle().Which.Should().Be(nameof(EmailModel.Email));
    }

    private static List<ValidationResult> Validate(string? email)
    {
        var model = new BareEmailModel { Email = email };
        var context = new ValidationContext(model) { MemberName = nameof(BareEmailModel.Email) };
        var results = new List<ValidationResult>();

        Validator.TryValidateProperty(email, context, results);
        return results;
    }

    /// <summary>A model carrying the rule alone, with the attribute's own default message.</summary>
    private sealed class BareEmailModel
    {
        [OptionalEmail]
        public string? Email { get; init; }
    }

    /// <summary>A model whose message is a localization resource key, the shipped idiom.</summary>
    private sealed class EmailModel
    {
        [OptionalEmail(ErrorMessage = "Auth.Field.Email.Invalid")]
        public string? Email { get; init; }
    }

    /// <summary>A localizer that knows only the keys it is handed.</summary>
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
