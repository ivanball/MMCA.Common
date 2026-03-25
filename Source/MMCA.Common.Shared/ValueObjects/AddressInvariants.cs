using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.ValueObjects;

/// <summary>
/// Invariant checks and field length constraints for <see cref="Address"/> value objects.
/// Max-length constants are shared with EF entity configurations and FluentValidation validators.
/// </summary>
public static class AddressInvariants
{
    /// <summary>Maximum length for <see cref="Address.AddressLine1"/>.</summary>
    public static readonly int AddressLine1MaxLength = 200;

    /// <summary>Maximum length for <see cref="Address.AddressLine2"/>.</summary>
    public static readonly int AddressLine2MaxLength = 200;

    /// <summary>Maximum length for <see cref="Address.City"/>.</summary>
    public static readonly int CityMaxLength = 100;

    /// <summary>Maximum length for <see cref="Address.State"/>.</summary>
    public static readonly int StateMaxLength = 100;

    /// <summary>Maximum length for <see cref="Address.ZipCode"/>.</summary>
    public static readonly int ZipCodeMaxLength = 20;

    /// <summary>Maximum length for <see cref="Address.Country"/>.</summary>
    public static readonly int CountryMaxLength = 100;

    /// <summary>
    /// Validates a full address. Null addresses are considered valid (address is optional on many entities).
    /// </summary>
    /// <param name="address">The address to validate, or <see langword="null"/>.</param>
    /// <param name="source">The calling method name, attached to any error for diagnostics.</param>
    /// <returns>A success result if valid, or a failure with invariant errors.</returns>
    public static Result EnsureAddressIsValid(Address? address, string source)
    {
        if (address is null)
            return Result.Success();

        return Result.Combine(
                EnsureAddressLine1IsValid(address.AddressLine1 ?? string.Empty, source));
    }

    /// <summary>
    /// Validates that address line 1 is not empty or whitespace.
    /// </summary>
    /// <param name="addressLine1">The address line 1 value to validate.</param>
    /// <param name="source">The calling method name, attached to any error for diagnostics.</param>
    /// <returns>A success result if valid, or a failure with an invariant error.</returns>
    public static Result EnsureAddressLine1IsValid(string addressLine1, string source)
        => string.IsNullOrWhiteSpace(addressLine1)
            ? Result.Failure(Error.Invariant(
                code: "Address.Line1.Empty",
                message: "Address Line 1 cannot be empty.",
                source: source,
                target: nameof(addressLine1)))
            : Result.Success();
}
