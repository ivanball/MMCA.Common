using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Gateway;

/// <summary>
/// Enforces the data annotations on the objects nested inside <see cref="GatewaySettings"/>:
/// <see cref="GatewaySettings.TraceHeaders"/> and both halves of
/// <see cref="GatewaySettings.HealthCheckDefaults"/>. <c>ValidateDataAnnotations</c> reads only the
/// attributes on <see cref="GatewaySettings"/>'s own properties and does not recurse, so without this a
/// blank trace-header name passed startup and then threw from every proxied request.
/// <para>
/// Used two ways, as the route policies are: <see cref="ThrowIfInvalid"/> fails registration for both
/// <c>AddMmcaGateway</c> overloads (ADR-070 fail-fast), and the configuration overload also registers
/// this as an <see cref="IValidateOptions{TOptions}"/>, so the value the options pipeline binds is
/// checked again when the host starts.
/// </para>
/// </summary>
internal sealed class GatewaySettingsValidator : IValidateOptions<GatewaySettings>
{
    /// <summary>Throws a <see cref="ValidationException"/> for the first nested value that fails.</summary>
    /// <param name="settings">The settings to check.</param>
    public static void ThrowIfInvalid(GatewaySettings settings)
    {
        foreach (var nested in NestedObjects(settings))
        {
            Validator.ValidateObject(nested, new ValidationContext(nested), validateAllProperties: true);
        }
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, GatewaySettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        foreach (var nested in NestedObjects(options))
        {
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(nested, new ValidationContext(nested), results, validateAllProperties: true))
            {
                failures.AddRange(results.Select(result =>
                    $"{GatewaySettings.SectionName}:{nested.GetType().Name}: {result.ErrorMessage}"));
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static object[] NestedObjects(GatewaySettings settings) =>
    [
        settings.TraceHeaders,
        settings.HealthCheckDefaults.Passive,
        settings.HealthCheckDefaults.Active,
    ];
}
