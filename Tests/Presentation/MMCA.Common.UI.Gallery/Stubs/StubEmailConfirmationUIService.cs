using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Gallery.Stubs;

/// <summary>
/// Canned <see cref="IEmailConfirmationUIService"/> for the gallery: a resend is always accepted (as the
/// real endpoint answers 202 for every well-formed address), so the resend prompt on <c>/login</c> and
/// <c>/register</c> reaches its "sent" state; a confirmation is refused, there being no real token.
/// </summary>
internal sealed class StubEmailConfirmationUIService : IEmailConfirmationUIService
{
    public Task<Result> ConfirmEmailAsync(string email, string token, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(Error.Validation("Gallery.ConfirmationUnavailable", "The gallery has no backend.")));

    public Task<Result> ResendEmailConfirmationAsync(string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
