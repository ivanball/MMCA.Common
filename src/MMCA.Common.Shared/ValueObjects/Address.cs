using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.ValueObjects;

/// <summary>
/// Immutable value object representing a postal address. Only <see cref="AddressLine1"/> is required;
/// all other fields are optional to support international address formats. Invariants are enforced
/// via <see cref="AddressInvariants"/> at creation time.
/// </summary>
/// <remarks>
/// Configured as an EF owned type via <c>OwnsOne</c> in entity configurations.
/// </remarks>
[DataContract]
public sealed record Address
{
    /// <summary>Gets the primary street address (required).</summary>
    [DataMember(Order = 1)]
    public string AddressLine1 { get; }

    /// <summary>Gets the secondary address line (e.g. apartment, suite).</summary>
    [DataMember(Order = 2)]
    public string? AddressLine2 { get; }

    /// <summary>Gets the city or locality name.</summary>
    [DataMember(Order = 3)]
    public string? City { get; }

    /// <summary>Gets the state, province, or region.</summary>
    [DataMember(Order = 4)]
    public string? State { get; }

    /// <summary>Gets the postal or ZIP code.</summary>
    [DataMember(Order = 5)]
    public string? ZipCode { get; }

    /// <summary>Gets the country name.</summary>
    [DataMember(Order = 6)]
    public string? Country { get; }

    [JsonConstructor]
    private Address(
        string addressLine1,
        string? addressLine2,
        string? city,
        string? state,
        string? zipCode,
        string? country)
    {
        AddressLine1 = addressLine1;
        AddressLine2 = addressLine2;
        City = city;
        State = state;
        ZipCode = zipCode;
        Country = country;
    }

    /// <summary>
    /// Factory method that creates an <see cref="Address"/> after validating invariants.
    /// </summary>
    /// <param name="addressLine1">The primary street address (required, validated by <see cref="AddressInvariants"/>).</param>
    /// <param name="addressLine2">Optional secondary address line.</param>
    /// <param name="city">Optional city or locality.</param>
    /// <param name="state">Optional state, province, or region.</param>
    /// <param name="zipCode">Optional postal or ZIP code.</param>
    /// <param name="country">Optional country name.</param>
    /// <returns>A success result with the address, or a failure if invariants are violated.</returns>
    public static Result<Address> Create(
        string addressLine1,
        string? addressLine2,
        string? city,
        string? state,
        string? zipCode,
        string? country)
    {
        var result = Result.Combine(
            AddressInvariants.EnsureAddressLine1IsValid(addressLine1, nameof(Create)));
        if (result.IsFailure)
            return Result.Failure<Address>(result.Errors);

        return Result.Success(new Address(
            addressLine1,
            addressLine2,
            city,
            state,
            zipCode,
            country));
    }

    /// <summary>Formats the address as a comma-separated string, omitting empty parts.</summary>
    /// <returns>A human-readable address string.</returns>
    public override string ToString()
        => string.Join(
            ", ",
            new[]
            {
                AddressLine1,
                string.IsNullOrEmpty(AddressLine2) ? null : AddressLine2,
                City,
                string.IsNullOrEmpty(State) ? null : State,
                ZipCode,
                Country
            }.Where(part => !string.IsNullOrEmpty(part)));
}
