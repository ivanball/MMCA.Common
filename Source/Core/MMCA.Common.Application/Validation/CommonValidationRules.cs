using System.Linq.Expressions;
using FluentValidation;

namespace MMCA.Common.Application.Validation;

/// <summary>
/// Reusable validation rules for a required string field with a maximum length.
/// Module-specific validators compose these via <c>Include()</c> with their domain's
/// invariant constants for max length.
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class RequiredStringRules<T> : AbstractValidator<T>
{
    public RequiredStringRules(Expression<Func<T, string>> selector, string fieldName, int maxLength)
        => RuleFor(selector)
            .NotEmpty().WithMessage($"You must enter a {fieldName}")
            .MaximumLength(maxLength).WithMessage($"{fieldName} cannot be longer than {maxLength} characters");
}

/// <summary>
/// Reusable validation rules for an optional string field with a maximum length.
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class OptionalStringRules<T> : AbstractValidator<T>
{
    public OptionalStringRules(Expression<Func<T, string?>> selector, string fieldName, int maxLength)
        => RuleFor(selector)
            .MaximumLength(maxLength).WithMessage($"{fieldName} cannot be longer than {maxLength} characters");
}

/// <summary>
/// Reusable validation rules for a required email field: non-empty, valid format, max length.
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class EmailRules<T> : AbstractValidator<T>
{
    public EmailRules(Expression<Func<T, string>> selector, string fieldName, int maxLength)
        => RuleFor(selector)
            .NotEmpty().WithMessage($"You must enter a {fieldName}")
            .EmailAddress().WithMessage($"You must enter a valid {fieldName}")
            .MaximumLength(maxLength).WithMessage($"{fieldName} cannot be longer than {maxLength} characters");
}

/// <summary>
/// Reusable validation rules for an integer field that must be positive (greater than 0).
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class PositiveIntRules<T> : AbstractValidator<T>
{
    public PositiveIntRules(Expression<Func<T, int>> selector, string fieldName)
        => RuleFor(selector)
            .GreaterThan(0).WithMessage($"{fieldName} must be a positive value");
}

/// <summary>
/// Reusable validation rules for a decimal field that must be positive (greater than 0).
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class PositiveDecimalRules<T> : AbstractValidator<T>
{
    public PositiveDecimalRules(Expression<Func<T, decimal>> selector, string fieldName)
        => RuleFor(selector)
            .GreaterThan(0).WithMessage($"{fieldName} must be a positive value");
}

/// <summary>
/// Reusable validation rules for an integer field that must be non-negative (greater than or equal to 0).
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class NonNegativeIntRules<T> : AbstractValidator<T>
{
    public NonNegativeIntRules(Expression<Func<T, int>> selector, string fieldName)
        => RuleFor(selector)
            .GreaterThanOrEqualTo(0).WithMessage($"{fieldName} must be greater than or equal to 0");
}

/// <summary>
/// Reusable validation rules for a password field: required, min 8, max 128 characters.
/// For stricter complexity requirements, use <see cref="StrongPasswordRules{T}"/> instead.
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class PasswordRules<T> : AbstractValidator<T>
{
    public PasswordRules(Expression<Func<T, string>> selector)
        => RuleFor(selector)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.")
            .MaximumLength(128).WithMessage("Password cannot be longer than 128 characters.");
}

/// <summary>
/// Reusable validation rules for a password field with strong complexity requirements:
/// required, min 8, max 128, must contain uppercase, lowercase, digit, and special character.
/// </summary>
/// <typeparam name="T">The parent type containing the field.</typeparam>
public class StrongPasswordRules<T> : AbstractValidator<T>
{
    public StrongPasswordRules(Expression<Func<T, string>> selector)
        => RuleFor(selector)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.")
            .MaximumLength(128).WithMessage("Password cannot be longer than 128 characters.")
            .Matches("[A-Z]").WithMessage("Password must contain at least one uppercase letter.")
            .Matches("[a-z]").WithMessage("Password must contain at least one lowercase letter.")
            .Matches("\\d").WithMessage("Password must contain at least one digit.")
            .Matches("[^a-zA-Z\\d]").WithMessage("Password must contain at least one special character.");
}
