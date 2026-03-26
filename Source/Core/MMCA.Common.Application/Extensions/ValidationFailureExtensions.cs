using FluentValidation.Results;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Extensions;

/// <summary>
/// Extension methods for converting FluentValidation results to domain <see cref="Error"/> instances.
/// </summary>
public static class ValidationFailureExtensions
{
    extension(ValidationResult result)
    {
        /// <summary>
        /// Converts FluentValidation failures into domain <see cref="Error"/> instances
        /// with the <see cref="Error.Validation"/> error type.
        /// </summary>
        /// <param name="source">The source identifier (typically the handler or validator name).</param>
        /// <returns>An enumerable of domain errors.</returns>
        public IEnumerable<Error> ToErrors(string source)
            => result.Errors.Select(f =>
                Error.Validation(f.ErrorCode, f.ErrorMessage, source, f.PropertyName));
    }
}
