using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Application.Users.UseCases.SignInLinks;

/// <summary>
/// The contract an app's redeem-sign-in-link command implements: the request payload plus the client
/// details the opened session records, which only the controller can read.
/// </summary>
public interface IRedeemSignInLinkCommand : ICommandWithRequest<RedeemSignInLinkRequest>
{
    /// <summary>The caller's IP, recorded on the new refresh session.</summary>
    string? IpAddress { get; }

    /// <summary>The caller's user-agent, recorded on the new refresh session.</summary>
    string? UserAgent { get; }
}
