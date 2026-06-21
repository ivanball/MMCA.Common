using System.Collections.Frozen;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Auth;

/// <summary>
/// Abstract base for a role value object. Roles are stored as plain strings in the database and
/// emitted as JWT claims; this type adds canonical-value storage, case-insensitive value equality,
/// and validation against a per-app set of known role names (see <see cref="RoleNames"/>).
/// </summary>
/// <remarks>
/// Lives in <c>MMCA.Common.Shared</c> so it stays dependency-free and usable from Blazor WASM/UI as
/// well as Domain. Each app derives a concrete role type (e.g. <c>UserRole</c>) that fixes its own
/// role set (Store: Admin/Customer; ADC: Organizer/Attendee) and exposes app-specific factory
/// members, while inheriting the equality, hashing, and validation behavior defined here.
/// All role comparisons are case-insensitive, matching <c>ICurrentUserService.IsInRole</c>.
/// <para>
/// Intentionally does not implement <see cref="IEquatable{T}"/> (S4035: an unsealed
/// <c>IEquatable&lt;T&gt;</c> breaks the equality contract for subclasses). Equality is provided via
/// the <see cref="object.Equals(object?)"/> override below — type-guarded so two roles are equal only
/// when they are the same concrete type with the same value; a sealed derived type may add a
/// strongly-typed <c>IEquatable&lt;TSelf&gt;</c> and <c>==</c>/<c>!=</c> operators on top.
/// </para>
/// </remarks>
public abstract class RoleValue
{
    /// <summary>Gets the canonical string representation of the role.</summary>
    public string Value { get; }

    /// <summary>Initializes a new instance of the <see cref="RoleValue"/> class.</summary>
    /// <param name="value">The role's canonical string value.</param>
    protected RoleValue(string value) => Value = value;

    /// <summary>
    /// Validates that <paramref name="role"/> is one of the supplied known role names
    /// (case-insensitive). Use this from a derived type's factory or an invariant check.
    /// </summary>
    /// <param name="role">The role string to validate.</param>
    /// <param name="knownRoles">The set of valid role names for the app.</param>
    /// <param name="source">Calling method name, attached to any error for diagnostics.</param>
    /// <returns>A success result if valid, or an invariant failure with code <c>User.Role.Invalid</c>.</returns>
    public static Result Validate(string role, IReadOnlySet<string> knownRoles, string source)
    {
        ArgumentNullException.ThrowIfNull(knownRoles);

        return knownRoles.Contains(role ?? string.Empty)
            ? Result.Success()
            : Result.Failure(Error.Invariant(
                code: "User.Role.Invalid",
                message: $"Role '{role}' is not a valid user role.",
                source: source,
                target: nameof(role)));
    }

    /// <summary>
    /// Builds a case-insensitive, frozen lookup of the supplied role instances keyed by their
    /// <see cref="Value"/>. Intended for a derived type to back its <c>FromString</c>/<c>IsValid</c>
    /// members with interned singletons.
    /// </summary>
    /// <typeparam name="TRole">The concrete role type.</typeparam>
    /// <param name="roles">The canonical role instances.</param>
    /// <returns>A frozen, case-insensitive dictionary from role value to instance.</returns>
    protected static FrozenDictionary<string, TRole> BuildLookup<TRole>(params TRole[] roles)
        where TRole : RoleValue
    {
        ArgumentNullException.ThrowIfNull(roles);

        return roles.ToFrozenDictionary(
            r => r.Value,
            r => r,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) =>
        obj is RoleValue other
        && GetType() == other.GetType()
        && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);
}
