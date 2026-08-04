namespace MMCA.Common.Infrastructure.Persistence.DbContexts.Seeding;

/// <summary>
/// One development/test account for <see cref="IdentityModuleDbSeederBase{TUser}"/> to create.
/// </summary>
/// <remarks>
/// <strong>Security notice:</strong> seed credentials are plaintext by construction, so the list an
/// app supplies is development-only data. Gate it (see
/// <see cref="IdentityModuleDbSeederBase{TUser}.ShouldSeed"/>) or replace it with
/// environment-sourced secrets before running a seeder in a deployed environment.
/// </remarks>
/// <param name="Email">The account email; also the idempotency key ("already seeded?" check).</param>
/// <param name="Password">The plaintext password, hashed by the seeder before persistence.</param>
/// <param name="Role">The role to create the account with, as the app's role vocabulary spells it.</param>
/// <param name="FirstName">The given name, when the app's <c>User</c> carries one.</param>
/// <param name="LastName">The family name, when the app's <c>User</c> carries one.</param>
public sealed record SeedAccount(
    string Email,
    string Password,
    string Role,
    string? FirstName = null,
    string? LastName = null);
