using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.Aspire.Security;

/// <summary>
/// Validates a list of request path prefixes: every non-blank entry must start with <c>/</c>.
/// <see cref="Microsoft.AspNetCore.Http.PathString"/> refuses a non-empty value without a leading
/// slash, so an entry such as <c>"hubs"</c> would otherwise throw on every request that reaches the
/// matcher instead of failing once at startup (ADR-070). Blank entries are skipped, as the matchers
/// skip them.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class LeadingSlashPathPrefixesAttribute : ValidationAttribute
{
    /// <inheritdoc />
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        ArgumentNullException.ThrowIfNull(validationContext);

        if (value is not IEnumerable<string> prefixes)
        {
            return ValidationResult.Success;
        }

        string[] offenders = [.. prefixes.Where(prefix => !string.IsNullOrWhiteSpace(prefix) && !prefix.StartsWith('/'))];

        return offenders.Length == 0
            ? ValidationResult.Success
            : new ValidationResult(
                $"{validationContext.MemberName} entries must start with '/': {string.Join(", ", offenders)}",
                [validationContext.MemberName!]);
    }
}
