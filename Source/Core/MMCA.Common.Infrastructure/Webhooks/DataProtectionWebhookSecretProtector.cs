using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using MMCA.Common.Application.Webhooks;
using MMCA.Common.Application.Webhooks.Subscriptions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Infrastructure.Webhooks;

/// <summary>
/// Default <see cref="IWebhookSecretProtector"/> over the host's data-protection key ring, under the
/// purpose <c>MMCA.Common.Webhooks.Secret.v1</c>. A multi-replica host needs the shared key ring
/// (ADR-069), or a replica cannot read a secret another one wrote; that surfaces as
/// <c>Webhooks.SecretUnreadable</c>, never as an exception.
/// </summary>
/// <param name="provider">The data-protection provider.</param>
internal sealed class DataProtectionWebhookSecretProtector(IDataProtectionProvider provider) : IWebhookSecretProtector
{
    /// <summary>The data-protection purpose; part of the stored format, so it never changes.</summary>
    internal const string Purpose = "MMCA.Common.Webhooks.Secret.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    /// <inheritdoc />
    public string Protect(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return _protector.Protect(secret);
    }

    /// <inheritdoc />
    public Result<string> Unprotect(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return Unreadable();
        }

        try
        {
            return Result.Success(_protector.Unprotect(protectedSecret));
        }
        catch (CryptographicException)
        {
            return Unreadable();
        }
        catch (FormatException)
        {
            return Unreadable();
        }
    }

    private static Result<string> Unreadable() =>
        Result.Failure<string>(Error.Failure(
            WebhookErrorCodes.SecretUnreadable,
            "The webhook signing secret cannot be unprotected with this host's key ring.",
            nameof(Unprotect)));
}
