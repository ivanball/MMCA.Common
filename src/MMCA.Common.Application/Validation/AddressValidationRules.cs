using System.Linq.Expressions;
using FluentValidation;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Application.Validation;

/// <summary>
/// Composite FluentValidation validator for the <see cref="Address"/> value object.
/// Includes individual field rule sets so they can be reused independently when
/// validating request DTOs that embed address fields without an <see cref="Address"/> wrapper.
/// </summary>
public sealed class AddressValidator : AbstractValidator<Address>
{
    public AddressValidator()
    {
        Include(new AddressLine1Rules<Address>(p => p.AddressLine1));
        Include(new AddressLine2Rules<Address>(p => p.AddressLine2));
        Include(new CityRules<Address>(p => p.City));
        Include(new StateRules<Address>(p => p.State));
        Include(new ZipCodeRules<Address>(p => p.ZipCode));
        Include(new CountryRules<Address>(p => p.Country));
    }
}

/// <summary>
/// Reusable validation rules for AddressLine1. Accepts a selector expression so the same
/// rules can apply to any parent type containing an address line 1 field.
/// </summary>
/// <typeparam name="T">The parent type containing the address field.</typeparam>
public sealed class AddressLine1Rules<T>
    : AbstractValidator<T>
{
    public AddressLine1Rules(Expression<Func<T, string>> selector)
        => RuleFor(selector)
            .NotEmpty().WithMessage("You must enter an Address Line 1")
            .MaximumLength(AddressInvariants.AddressLine1MaxLength).WithMessage($"Address Line 1 cannot be longer than {AddressInvariants.AddressLine1MaxLength} characters");
}

/// <summary>Reusable validation rules for AddressLine2 (optional field, max-length only).</summary>
/// <typeparam name="T">The parent type containing the address field.</typeparam>
public sealed class AddressLine2Rules<T>
    : AbstractValidator<T>
{
    public AddressLine2Rules(Expression<Func<T, string?>> selector)
        => RuleFor(selector)
            .MaximumLength(AddressInvariants.AddressLine2MaxLength).WithMessage($"Address Line 2 cannot be longer than {AddressInvariants.AddressLine2MaxLength} characters");
}

/// <summary>Reusable validation rules for City (optional field, max-length only).</summary>
/// <typeparam name="T">The parent type containing the address field.</typeparam>
public sealed class CityRules<T>
    : AbstractValidator<T>
{
    public CityRules(Expression<Func<T, string?>> selector)
        => RuleFor(selector)
            .MaximumLength(AddressInvariants.CityMaxLength).WithMessage($"City cannot be longer than {AddressInvariants.CityMaxLength} characters");
}

/// <summary>Reusable validation rules for State (optional field, max-length only).</summary>
/// <typeparam name="T">The parent type containing the address field.</typeparam>
public sealed class StateRules<T>
    : AbstractValidator<T>
{
    public StateRules(Expression<Func<T, string?>> selector)
        => RuleFor(selector)
            .MaximumLength(AddressInvariants.StateMaxLength).WithMessage($"State cannot be longer than {AddressInvariants.StateMaxLength} characters");
}

/// <summary>Reusable validation rules for ZipCode (optional field, max-length only).</summary>
/// <typeparam name="T">The parent type containing the address field.</typeparam>
public sealed class ZipCodeRules<T>
    : AbstractValidator<T>
{
    public ZipCodeRules(Expression<Func<T, string?>> selector)
        => RuleFor(selector)
            .MaximumLength(AddressInvariants.ZipCodeMaxLength).WithMessage($"Zip Code cannot be longer than {AddressInvariants.ZipCodeMaxLength} characters");
}

/// <summary>Reusable validation rules for Country (optional field, max-length only).</summary>
/// <typeparam name="T">The parent type containing the address field.</typeparam>
public sealed class CountryRules<T>
    : AbstractValidator<T>
{
    public CountryRules(Expression<Func<T, string?>> selector)
        => RuleFor(selector)
            .MaximumLength(AddressInvariants.CountryMaxLength).WithMessage($"Country cannot be longer than {AddressInvariants.CountryMaxLength} characters");
}
