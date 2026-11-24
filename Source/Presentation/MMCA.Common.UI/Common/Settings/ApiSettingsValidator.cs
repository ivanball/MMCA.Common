using Microsoft.Extensions.Options;

namespace MMCA.Common.UI.Common.Settings;

/// <summary>
/// The source-generated validator for <see cref="ApiSettings"/>: checks the same data annotations
/// <c>ValidateDataAnnotations</c> would, without reflection, so it also runs on a trimmed WebAssembly
/// client that carries no <c>Microsoft.Extensions.Options.DataAnnotations</c>.
/// </summary>
[OptionsValidator]
internal sealed partial class ApiSettingsValidator : IValidateOptions<ApiSettings>;
