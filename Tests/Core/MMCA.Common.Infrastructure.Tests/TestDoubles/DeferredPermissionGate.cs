using System.Globalization;
using System.Security.Claims;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.InternalCommands;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Infrastructure.Tests.TestDoubles;

/// <summary>
/// L181: the authorization gate a deferred hop must satisfy exactly as the inline request did. The
/// principal holds the permission ONLY as a <see cref="AuthClaimTypes.Permission"/> claim (the way a
/// stored grant reaches a service that does not own the grant table) and the registry grants it to
/// no role, so the claim is the one thing that can let the command through.
/// </summary>
internal static class DeferredPermissionGate
{
    /// <summary>The permission the gated command requires.</summary>
    public const string RequiredPermission = "reports:export";

    /// <summary>A second claim-only permission, so a test can prove every claim survives, not just the first.</summary>
    public const string OtherPermission = "reports:schedule";

    /// <summary>Builds the inline principal: a subject, no roles, and the permission as claims only.</summary>
    /// <param name="userId">The subject.</param>
    /// <returns>The principal.</returns>
    public static ClaimsPrincipal ClaimOnlyPrincipal(UserIdentifierType userId) =>
        new(new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.Subject, userId.ToString(CultureInfo.InvariantCulture)),
                new Claim(AuthClaimTypes.Permission, RequiredPermission),
                new Claim(AuthClaimTypes.Permission, OtherPermission),
            ],
            "Bearer"));

    /// <summary>Wraps <paramref name="inner"/> in the real authorization decorator over a registry that grants nothing.</summary>
    /// <param name="inner">The handler the gate guards.</param>
    /// <param name="currentUser">The principal the gate evaluates.</param>
    /// <returns>The decorated handler.</returns>
    public static ICommandHandler<GatedCommand, Result> Decorate(
        ICommandHandler<GatedCommand, Result> inner,
        ICurrentUserService currentUser) =>
        new AuthorizationCommandDecorator<GatedCommand, Result>(inner, currentUser, new PermissionRegistryBuilder().Build());

    /// <summary>Runs the gate for <paramref name="currentUser"/> over a handler that only succeeds.</summary>
    /// <param name="currentUser">The principal the gate evaluates.</param>
    /// <returns>Success when the gate let the command through.</returns>
    public static Task<Result> EvaluateAsync(ICurrentUserService currentUser) =>
        Decorate(new GatedCommandHandler(), currentUser).HandleAsync(new GatedCommand("probe"));

    /// <summary>A deferrable command that requires <see cref="RequiredPermission"/>.</summary>
    /// <param name="Value">Free-form payload.</param>
    internal sealed record GatedCommand(string Value) : IInternalCommand, IRequiresPermission
    {
        /// <inheritdoc />
        public string Permission => RequiredPermission;
    }

    /// <summary>Counts the executions the gate let through.</summary>
    internal sealed class GatedCommandHandler : ICommandHandler<GatedCommand, Result>
    {
        public int Executions { get; private set; }

        public Task<Result> HandleAsync(GatedCommand command, CancellationToken cancellationToken = default)
        {
            Executions++;
            return Task.FromResult(Result.Success());
        }
    }

    /// <summary>The inline caller: answers from the claim-only principal.</summary>
    /// <param name="principal">The principal to report.</param>
    internal sealed class FixedCurrentUserService(ClaimsPrincipal principal) : ICurrentUserService
    {
        public ClaimsPrincipal User => principal;

        public UserIdentifierType? UserId =>
            UserIdentifierType.TryParse(principal.FindFirst(AuthClaimTypes.Subject)?.Value, CultureInfo.InvariantCulture, out var id)
                ? id
                : null;

        public string? Role => null;

        public T? GetClaimValue<T>(string claimType)
            where T : struct, IParsable<T> => null;
    }
}
