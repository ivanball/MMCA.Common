using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.UI.Pages.Auth;

/// <summary>
/// Code-behind for the <c>/register</c> page: the optional postal address block, which the host
/// can turn off through <c>RegistrationSettings.CollectAddress</c>.
/// </summary>
public partial class Register
{
    /// <summary>Gets a value indicating whether the host offers the optional address block at all.</summary>
    private bool CollectAddress => RegistrationOptions.Value.CollectAddress;

    /// <summary>
    /// Null means "no address entered", which is legitimate: the address block is optional. Anything
    /// the user DID type is validated, and an invalid address blocks the registration instead of
    /// being silently dropped (the account used to be created with no address at all). With address
    /// collection off there is no block to type into, so there is nothing to validate or send.
    /// </summary>
    /// <returns>The address result, or null when there is no address to send.</returns>
    private Result<Address>? BuildAddressResult()
    {
        if (!CollectAddress)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_model.AddressLine1)
            && string.IsNullOrWhiteSpace(_model.AddressLine2)
            && string.IsNullOrWhiteSpace(_model.City)
            && string.IsNullOrWhiteSpace(_model.State)
            && string.IsNullOrWhiteSpace(_model.ZipCode)
            && string.IsNullOrWhiteSpace(_model.Country))
        {
            return null;
        }

        return Address.Create(_model.AddressLine1, _model.AddressLine2, _model.City, _model.State, _model.ZipCode, _model.Country);
    }
}
