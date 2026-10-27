using System.Linq.Expressions;
using FluentValidation;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Application.Validation;

/// <summary>
/// FluentValidation validator for a United States <see cref="Address"/>, opt-in for apps that take
/// US addresses: on top of the length rules every address gets (<see cref="AddressValidator"/>), the
/// street line, city, state and ZIP code are required, the state must be one of
/// <see cref="UsPostalRegions"/> (50 states, DC, territories and the military regions) and the ZIP
/// code must be 5 digits or ZIP+4. The country is not checked: the caller decides which addresses are
/// US ones. The plain <see cref="AddressValidator"/> is unchanged.
/// </summary>
public sealed class UsAddressValidator : AbstractValidator<Address>
{
    public UsAddressValidator()
    {
        Include(new AddressValidator());
        Include(new UsCityRules<Address>(p => p.City));
        Include(new UsStateRules<Address>(p => p.State));
        Include(new UsZipCodeRules<Address>(p => p.ZipCode));
    }
}

/// <summary>Reusable rule: a US address's city is required.</summary>
/// <typeparam name="T">The parent type containing the address field.</typeparam>
public sealed class UsCityRules<T>
    : AbstractValidator<T>
{
    public UsCityRules(Expression<Func<T, string?>> selector)
        => RuleFor(selector)
            .NotEmpty().WithMessage("You must enter a City");
}

/// <summary>Reusable rule: a US address's state is required and must be a USPS region code.</summary>
/// <typeparam name="T">The parent type containing the address field.</typeparam>
public sealed class UsStateRules<T>
    : AbstractValidator<T>
{
    public UsStateRules(Expression<Func<T, string?>> selector)
        => RuleFor(selector)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("You must enter a State")
            .Must(UsPostalRegions.IsKnownCode).WithMessage("State must be a two-letter US state, territory or military region code");
}

/// <summary>Reusable rule: a US address's ZIP code is required and must be 5 digits or ZIP+4.</summary>
/// <typeparam name="T">The parent type containing the address field.</typeparam>
public sealed class UsZipCodeRules<T>
    : AbstractValidator<T>
{
    public UsZipCodeRules(Expression<Func<T, string?>> selector)
        => RuleFor(selector)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("You must enter a Zip Code")
            .Must(UsPostalRegions.IsValidZipCode).WithMessage("Zip Code must be 5 digits or ZIP+4 (12345-6789)");
}
