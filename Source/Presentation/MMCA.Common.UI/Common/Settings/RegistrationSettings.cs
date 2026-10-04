namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// Strongly-typed options bound to the <c>"Registration"</c> configuration section: which optional
/// blocks the shared register page offers. Every option defaults to the page's long-standing
/// behaviour, so a host that configures nothing sees no change.
/// </summary>
public sealed class RegistrationSettings
{
    /// <summary>Configuration section name used for binding.</summary>
    public static readonly string SectionName = "Registration";

    /// <summary>
    /// Gets or sets a value indicating whether the register page offers the optional postal address
    /// block. <see langword="true"/> (the default) shows it; <see langword="false"/> hides it and the
    /// registration request carries no address.
    /// </summary>
    public bool CollectAddress { get; set; } = true;
}
