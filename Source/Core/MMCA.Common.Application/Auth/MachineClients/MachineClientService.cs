using System.Security.Claims;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth.MachineClients;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.MachineClients;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Application.Auth.MachineClients;

/// <summary>The framework's <see cref="IMachineClientService"/> (ADR-140).</summary>
/// <remarks>
/// <para>
/// <b>Minted by the token service directly.</b> The injected <see cref="ITokenService"/> is the raw
/// signer, not the session-stamping wrapper the sign-in workflow mints through (the view-as
/// precedent), so a machine token carries no <c>sid</c>, no <c>mfa</c> and no <c>auth_time</c>, and no
/// refresh session is opened for it. It carries the service account's standard claims and its role's
/// permissions exactly as a person's token does, plus <c>client_id</c> and <c>token_use=machine</c>.
/// </para>
/// <para>
/// <b>Four refused permissions on the role</b>, checked at create AND at every mint (a role can gain a
/// permission after the client was created): a role granting <c>users:view-as</c>,
/// <c>users:manage</c>, <c>roles:manage</c> or <c>clients:manage</c> never backs a client.
/// </para>
/// </remarks>
/// <param name="store">The client rows.</param>
/// <param name="provisioner">The consumer's service-account reader and creator.</param>
/// <param name="tokenService">The raw token signer.</param>
/// <param name="permissions">The host's permission registry, read for the role guardrail.</param>
/// <param name="cache">The shared cache the revocation marker is written to.</param>
/// <param name="settings">The machine-client settings.</param>
/// <param name="timeProvider">The clock.</param>
public sealed class MachineClientService(
    IMachineClientStore store,
    IServiceAccountProvisioner provisioner,
    ITokenService tokenService,
    IPermissionRegistry permissions,
    ICacheService cache,
    IOptions<MachineClientSettings> settings,
    TimeProvider timeProvider) : IMachineClientService
{
    /// <summary>The only grant type the token endpoint serves.</summary>
    public const string ClientCredentialsGrantType = "client_credentials";

    /// <summary>
    /// The permissions no machine client may hold: impersonation and user management, plus the two
    /// that would let a client escalate on its own: role management (it could grant its role anything,
    /// impersonation included) and client management (it could mint further clients for itself).
    /// </summary>
    private static readonly string[] ForbiddenPermissions =
    [
        AdministrationPermissions.ViewAsUsers,
        AdministrationPermissions.ManageUsers,
        AdministrationPermissions.ManageRoles,
        AdministrationPermissions.ManageClients,
    ];

    /// <inheritdoc />
    public bool IsEnabled => settings.Value.Enabled;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public async Task<Result<MachineTokenResponse>> IssueTokenAsync(
        string? grantType,
        string? clientId,
        string? clientSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(grantType))
        {
            return Result.Failure<MachineTokenResponse>(MachineClientErrors.InvalidRequest(nameof(IssueTokenAsync)));
        }

        if (!string.Equals(grantType, ClientCredentialsGrantType, StringComparison.Ordinal))
        {
            return Result.Failure<MachineTokenResponse>(MachineClientErrors.UnsupportedGrantType(nameof(IssueTokenAsync)));
        }

        var invalidClient = Result.Failure<MachineTokenResponse>(MachineClientErrors.InvalidClient(nameof(IssueTokenAsync)));
        if (!Guid.TryParse(clientId, out var id) || string.IsNullOrEmpty(clientSecret))
        {
            return invalidClient;
        }

        var now = UtcNow;
        var client = await store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (client is null || !client.Accepts(clientSecret, now))
        {
            return invalidClient;
        }

        // The account is read on every mint, so a deleted or locked account, or a role that has gained
        // a forbidden permission since the client was created, stops the client at once.
        var account = await provisioner.FindAsync(client.ServiceAccountUserId, cancellationToken).ConfigureAwait(false);
        if (account is null || account.UserId != client.ServiceAccountUserId || IsForbiddenRole(account.Role))
        {
            return invalidClient;
        }

        var lifetime = TimeSpan.FromMinutes(settings.Value.AccessTokenMinutes);
        Claim[] machineClaims =
        [
            new(AuthClaimTypes.ClientId, client.Id.ToString()),
            new(AuthClaimTypes.TokenUse, AuthClaimTypes.TokenUseMachine),
        ];
        var token = tokenService.GenerateShortLivedAccessToken(
            account.UserId,
            account.Email,
            account.Role,
            account.Name,
            machineClaims,
            lifetime);

        await store.RecordUseAsync(client, now, cancellationToken).ConfigureAwait(false);

        return Result.Success(new MachineTokenResponse(token, "Bearer", (int)lifetime.TotalSeconds));
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<MachineClientResponse>>> ListAsync(CancellationToken cancellationToken = default)
    {
        var clients = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<MachineClientResponse>>([.. clients.Select(ToResponse)]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The role is checked BEFORE the service account is provisioned, so a refused create leaves no
    /// orphaned account behind. The client row's own validation (the name, the expiry) also runs
    /// first, on a throwaway hash.
    /// </remarks>
    public async Task<Result<MachineClientSecretResponse>> CreateAsync(
        CreateMachineClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Role))
        {
            return Result.Failure<MachineClientSecretResponse>(MachineClientErrors.RoleRequired(nameof(CreateAsync)));
        }

        if (IsForbiddenRole(request.Role))
        {
            return Result.Failure<MachineClientSecretResponse>(MachineClientErrors.RoleNotAllowed(nameof(CreateAsync)));
        }

        var now = UtcNow;
        var secret = MachineClientSecret.Generate();
        var secretHash = MachineClientSecret.Hash(secret);
        var validated = MachineClient.Create(request.Name, default, secretHash, request.SecretExpiresOn, now);
        if (validated.IsFailure)
        {
            return Result.Failure<MachineClientSecretResponse>(validated.Errors);
        }

        var provisioned = await provisioner.CreateAsync(request.Name.Trim(), request.Role, cancellationToken).ConfigureAwait(false);
        if (!provisioned.TryGetValue(out var serviceAccountUserId))
        {
            return Result.Failure<MachineClientSecretResponse>(provisioned.Errors);
        }

        var client = MachineClient.Create(request.Name, serviceAccountUserId, secretHash, request.SecretExpiresOn, now).Value!;
        await store.AddAsync(client, cancellationToken).ConfigureAwait(false);

        return Result.Success(new MachineClientSecretResponse(ToResponse(client), secret));
    }

    /// <inheritdoc />
    public async Task<Result<MachineClientSecretResponse>> RotateAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        var client = await store.FindAsync(clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return Result.Failure<MachineClientSecretResponse>(MachineClientErrors.NotFound(nameof(RotateAsync)));
        }

        var secret = MachineClientSecret.Generate();
        var rotated = client.Rotate(MachineClientSecret.Hash(secret));
        if (rotated.IsFailure)
        {
            return Result.Failure<MachineClientSecretResponse>(rotated.Errors);
        }

        await store.SaveAsync(client, cancellationToken).ConfigureAwait(false);
        return Result.Success(new MachineClientSecretResponse(ToResponse(client), secret));
    }

    /// <inheritdoc />
    public async Task<Result<MachineClientResponse>> PromoteAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        var client = await store.FindAsync(clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return Result.Failure<MachineClientResponse>(MachineClientErrors.NotFound(nameof(PromoteAsync)));
        }

        var promoted = client.Promote();
        if (promoted.IsFailure)
        {
            return Result.Failure<MachineClientResponse>(promoted.Errors);
        }

        await store.SaveAsync(client, cancellationToken).ConfigureAwait(false);
        return Result.Success(ToResponse(client));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The row is saved first, so no new token is minted from here on, THEN the soft-deleted marker is
    /// written for the service account: every service's <c>SoftDeletedUserMiddleware</c> then answers
    /// 401 to the tokens the client already holds. The marker means what it always means (reject this
    /// user id) and outlives every machine token, whose lifetime is capped below the marker's.
    /// </remarks>
    public async Task<Result<MachineClientResponse>> RevokeAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        var client = await store.FindAsync(clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return Result.Failure<MachineClientResponse>(MachineClientErrors.NotFound(nameof(RevokeAsync)));
        }

        client.Revoke(UtcNow);
        await store.SaveAsync(client, cancellationToken).ConfigureAwait(false);
        await SoftDeletedUserCache.MarkDeletedAsync(cache, client.ServiceAccountUserId, cancellationToken).ConfigureAwait(false);

        return Result.Success(ToResponse(client));
    }

    private static MachineClientResponse ToResponse(MachineClient client) =>
        new(
            client.Id,
            client.Name,
            client.ServiceAccountUserId,
            client.CreatedOn,
            client.SecretExpiresOn,
            client.RevokedOn,
            client.LastUsedOn,
            client.SecondarySecretHash is not null);

    private bool IsForbiddenRole(string role)
    {
        var granted = permissions.GetPermissions(role);
        return ForbiddenPermissions.Any(granted.Contains);
    }
}
