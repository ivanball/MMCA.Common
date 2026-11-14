using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace MMCA.Common.API.Uploads;

/// <summary>
/// Reads the request form before model binding for an action that binds a form or a form file, so a
/// body over the endpoint's size limit answers 413 instead of 400. Model binding catches the
/// server's "request body too large" refusal and turns it into a model-state error ("Failed to read
/// the request form"), which <c>[ApiController]</c> answers as 400; read here, the same refusal is a
/// <see cref="BadHttpRequestException"/> with status 413 that reaches <c>GlobalExceptionHandler</c>,
/// which answers it as "Payload Too Large".
/// <para>
/// Only that one refusal changes. A malformed form is left for model binding to report exactly as
/// before, and an action that binds no form (a streaming upload that reads the body itself) is not
/// touched, so its body is never buffered here. The form read here is cached on the request, so
/// binding does not read the body twice.
/// </para>
/// </summary>
internal sealed class OversizeFormResourceFilter : IAsyncResourceFilter
{
    /// <summary>
    /// The global registration order: after <c>[RequestSizeLimit]</c> and <c>[RequestFormLimits]</c> (both
    /// order 900), so the endpoint's own limits are applied before the body is read. A type-registered
    /// filter takes its order from the registration, not from the instance.
    /// </summary>
    internal const int FilterOrder = 1000;

    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.HttpContext.Request;
        if (request.HasFormContentType && BindsForm(context.ActionDescriptor))
        {
            try
            {
                await request.ReadFormAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode != StatusCodes.Status413PayloadTooLarge)
            {
                // Not an oversize body: model binding reports it as it always has.
            }
            catch (InvalidDataException)
            {
                // A malformed form or one over a form limit: model binding reports it as before.
            }
        }

        await next().ConfigureAwait(false);
    }

    private static bool BindsForm(ActionDescriptor action) =>
        action.Parameters.Any(static parameter =>
            parameter.BindingInfo?.BindingSource is { } source
            && (source == BindingSource.Form || source == BindingSource.FormFile));
}
