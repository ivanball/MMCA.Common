using Microsoft.Extensions.Options;

namespace MMCA.Common.UI.Services.Diagnostics;

/// <summary>
/// The source-generated validator for <see cref="InAppDiagnosticsOptions"/>: checks the same data
/// annotations <c>ValidateDataAnnotations</c> would, without reflection.
/// </summary>
[OptionsValidator]
internal sealed partial class InAppDiagnosticsOptionsValidator : IValidateOptions<InAppDiagnosticsOptions>;
