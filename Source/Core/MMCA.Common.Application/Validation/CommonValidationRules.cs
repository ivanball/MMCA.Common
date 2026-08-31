using System.Globalization;
using System.Linq.Expressions;
using FluentValidation;
using MMCA.Common.Domain.Invariants;

namespace MMCA.Common.Application.Validation;

/// <summary>
/// Applies an optional error code to a rule without changing the rule when none is supplied.
/// </summary>
/// <remarks>
/// Every rule class in this file takes an optional <c>errorCode</c>. Module validators used to
/// bypass these bases entirely and re-write the rule by hand for the single reason that the bases
/// set a message but no code, so a validator that needed a machine-readable code had nothing to
/// compose. The code, when given, is applied to <b>every</b> rule the class declares for the field,
/// so one field answers under one code. A field whose bounds must answer under distinct codes still
/// declares its own rules.
/// </remarks>
internal static class OptionalErrorCodeExtensions
{
    /// <summary>
    /// Returns <paramref name="rule"/> unchanged when <paramref name="errorCode"/> is
    /// <see langword="null"/>, preserving the behaviour of every caller that omits it.
    /// </summary>
    /// <typeparam name="T">The type being validated.</typeparam>
    /// <typeparam name="TProperty">The type of the property the rule applies to.</typeparam>
    /// <param name="rule">The rule to apply the code to.</param>
    /// <param name="errorCode">The error code, or <see langword="null"/> to leave the rule as is.</param>
    /// <returns>The same rule, carrying the error code when one was supplied.</returns>
    internal static IRuleBuilderOptions<T, TProperty> WithOptionalErrorCode<T, TProperty>(
        this IRuleBuilderOptions<T, TProperty> rule, string? errorCode)
        => errorCode is null ? rule : rule.WithErrorCode(errorCode);
}

/// <summary>
/// Reusable validation rules for a required string field with a maximum length.
/// Module-specific validators compose these via <c>Include()</c> with their domain's
/// invariant constants for max length.
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class RequiredStringRules<T> : AbstractValidator<T>
{
    public RequiredStringRules(Expression<Func<T, string>> selector, string fieldName, int maxLength, string? errorCode = null)
        => RuleFor(selector)
            .NotEmpty().WithMessage($"You must enter a {fieldName}").WithOptionalErrorCode(errorCode)
            .MaximumLength(maxLength).WithMessage(string.Create(CultureInfo.InvariantCulture, $"{fieldName} cannot be longer than {maxLength} characters")).WithOptionalErrorCode(errorCode);
}

/// <summary>
/// Reusable validation rules for an optional string field with a maximum length.
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class OptionalStringRules<T> : AbstractValidator<T>
{
    public OptionalStringRules(Expression<Func<T, string?>> selector, string fieldName, int maxLength, string? errorCode = null)
        => RuleFor(selector)
            .MaximumLength(maxLength).WithMessage(string.Create(CultureInfo.InvariantCulture, $"{fieldName} cannot be longer than {maxLength} characters")).WithOptionalErrorCode(errorCode);
}

/// <summary>
/// Reusable validation rules for a required email field: non-empty, valid format, max length.
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class EmailRules<T> : AbstractValidator<T>
{
    public EmailRules(Expression<Func<T, string>> selector, string fieldName, int maxLength, string? errorCode = null)
        => RuleFor(selector)
            .NotEmpty().WithMessage($"You must enter a {fieldName}").WithOptionalErrorCode(errorCode)
            .EmailAddress().WithMessage($"You must enter a valid {fieldName}").WithOptionalErrorCode(errorCode)
            .MaximumLength(maxLength).WithMessage(string.Create(CultureInfo.InvariantCulture, $"{fieldName} cannot be longer than {maxLength} characters")).WithOptionalErrorCode(errorCode);
}

/// <summary>
/// Reusable validation rules for an optional absolute URL field: max length plus an absolute
/// <c>http</c>/<c>https</c> URI check. A <see langword="null"/> or empty value passes.
/// </summary>
/// <remarks>
/// The bounded-string treatment URL fields get today accepts <c>javascript:</c> and <c>data:</c>
/// values, which become executable the moment a link or an image renders them. This rule adds the
/// scheme check to the same length bound, and delegates it to
/// <see cref="CommonInvariants.EnsureUrlIsWellFormed"/> so the validator and the domain invariant
/// answer identically.
/// </remarks>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class AbsoluteUrlRules<T> : AbstractValidator<T>
{
    public AbsoluteUrlRules(Expression<Func<T, string?>> selector, string fieldName, int maxLength, string? errorCode = null)
        => RuleFor(selector)
            .MaximumLength(maxLength).WithMessage(string.Create(CultureInfo.InvariantCulture, $"{fieldName} cannot be longer than {maxLength} characters")).WithOptionalErrorCode(errorCode)
            .Must(BeAnAbsoluteHttpUrl).WithMessage($"{fieldName} must be an absolute http or https URL").WithOptionalErrorCode(errorCode);

    private static bool BeAnAbsoluteHttpUrl(string? url) =>
        CommonInvariants.EnsureUrlIsWellFormed(url, "Url.Invalid", "Url.Invalid", nameof(AbsoluteUrlRules<>), "url").IsSuccess;
}

/// <summary>
/// Reusable validation rules for an integer field that must be positive (greater than 0).
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class PositiveIntRules<T> : AbstractValidator<T>
{
    public PositiveIntRules(Expression<Func<T, int>> selector, string fieldName, string? errorCode = null)
        => RuleFor(selector)
            .GreaterThan(0).WithMessage($"{fieldName} must be a positive value").WithOptionalErrorCode(errorCode);
}

/// <summary>
/// Reusable validation rules for a decimal field that must be positive (greater than 0).
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class PositiveDecimalRules<T> : AbstractValidator<T>
{
    public PositiveDecimalRules(Expression<Func<T, decimal>> selector, string fieldName, string? errorCode = null)
        => RuleFor(selector)
            .GreaterThan(0).WithMessage($"{fieldName} must be a positive value").WithOptionalErrorCode(errorCode);
}

/// <summary>
/// Reusable validation rules for an integer field that must be non-negative (greater than or equal to 0).
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class NonNegativeIntRules<T> : AbstractValidator<T>
{
    public NonNegativeIntRules(Expression<Func<T, int>> selector, string fieldName, string? errorCode = null)
        => RuleFor(selector)
            .GreaterThanOrEqualTo(0).WithMessage($"{fieldName} must be greater than or equal to 0").WithOptionalErrorCode(errorCode);
}

/// <summary>
/// Reusable validation rules for a required identifier field: the field must carry a value that is
/// not the identifier type's default.
/// </summary>
/// <remarks>
/// <c>NotEmpty</c> is the deliberate check: it rejects zero for an integer key and
/// <see cref="Guid.Empty"/> for a <see cref="Guid"/> key, which is exactly what "an id was never
/// supplied" looks like on the wire for both shapes. The field phrase is interpolated verbatim
/// into "You must specify {fieldName}", so the caller supplies the article and any qualifier:
/// "a Category", "an Event for the Session".
/// </remarks>
/// <typeparam name="T">The parent type containing the field.</typeparam>
/// <typeparam name="TId">The identifier type of the field.</typeparam>
public class RequiredIdRules<T, TId> : AbstractValidator<T>
    where TId : notnull
{
    public RequiredIdRules(Expression<Func<T, TId>> selector, string fieldName, string? errorCode = null)
        => RuleFor(selector)
            .NotEmpty().WithMessage($"You must specify {fieldName}").WithOptionalErrorCode(errorCode);
}

/// <summary>
/// Reusable validation rules for an optional identifier field: when a value is supplied it must be
/// positive. A <see langword="null"/> value passes.
/// </summary>
/// <remarks>
/// FluentValidation skips a comparison rule when the nullable property holds
/// <see langword="null"/>, so the "when provided" part needs no <c>When</c> clause and no compiled
/// selector re-evaluated per validation pass.
/// </remarks>
/// <typeparam name="T">The parent type containing the field.</typeparam>
/// <typeparam name="TId">The underlying identifier type of the nullable field.</typeparam>
public class OptionalPositiveIdRules<T, TId> : AbstractValidator<T>
    where TId : struct, IComparable<TId>, IComparable
{
    public OptionalPositiveIdRules(Expression<Func<T, TId?>> selector, string fieldName, string? errorCode = null)
        => RuleFor(selector)
            .GreaterThan(default(TId)).WithMessage($"{fieldName} must be a valid positive value when provided.").WithOptionalErrorCode(errorCode);
}

/// <summary>
/// Reusable validation rules for a password field: required, min 8, max 128 characters.
/// For stricter complexity requirements, use <see cref="StrongPasswordRules{T}"/> instead.
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class PasswordRules<T> : AbstractValidator<T>
{
    public PasswordRules(Expression<Func<T, string>> selector, string? errorCode = null)
        => RuleFor(selector)
            .NotEmpty().WithMessage("Password is required.").WithOptionalErrorCode(errorCode)
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.").WithOptionalErrorCode(errorCode)
            .MaximumLength(128).WithMessage("Password cannot be longer than 128 characters.").WithOptionalErrorCode(errorCode);
}

/// <summary>
/// Reusable validation rules for a password field with strong complexity requirements:
/// required, min 8, max 128, must contain uppercase, lowercase, digit, and special character.
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class StrongPasswordRules<T> : AbstractValidator<T>
{
    public StrongPasswordRules(Expression<Func<T, string>> selector, string? errorCode = null)
        => RuleFor(selector)
            .NotEmpty().WithMessage("Password is required.").WithOptionalErrorCode(errorCode)
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.").WithOptionalErrorCode(errorCode)
            .MaximumLength(128).WithMessage("Password cannot be longer than 128 characters.").WithOptionalErrorCode(errorCode)
            .Matches("[A-Z]").WithMessage("Password must contain at least one uppercase letter.").WithOptionalErrorCode(errorCode)
            .Matches("[a-z]").WithMessage("Password must contain at least one lowercase letter.").WithOptionalErrorCode(errorCode)
            .Matches("\\d").WithMessage("Password must contain at least one digit.").WithOptionalErrorCode(errorCode)
            .Matches("[^a-zA-Z\\d]").WithMessage("Password must contain at least one special character.").WithOptionalErrorCode(errorCode);
}
