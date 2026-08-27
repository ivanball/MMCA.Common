using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Architecture.Tests.ErrorCodeFixtures;

/// <summary>
/// Compiled <c>Error</c> construction sites for <c>ErrorCatalogFitnessTests</c>. The rule reads codes
/// out of IL, so the catalog it must judge is compiled here: well-formed codes, a cross-type
/// collision, an unprefixed code, one code reused across two branches of a single type, a shared
/// framework static, and a code built at run time that no static scan can read.
/// </summary>
internal static class TicketErrors
{
    internal static Error NotFound() => Error.NotFoundError("Tickets.NotFound", "Ticket not found");

    internal static Error AlreadyClosed() => Error.Conflict("Tickets.AlreadyClosed", "Ticket already closed");
}

/// <summary>Ships the same code from a second type: the collision the uniqueness rule catches.</summary>
internal static class DuplicateTicketErrors
{
    internal static Error NotFound() => Error.NotFoundError("Tickets.NotFound", "The ticket is missing");
}

/// <summary>
/// Reuses one code across two branches of the same type. One error with two exits is not a collision,
/// so the uniqueness rule must stay silent about it.
/// </summary>
internal static class TwoBranchTicketErrors
{
    internal static Error Invalid(bool alternate) =>
        alternate
            ? Error.Validation("Tickets.Invalid", "Alternate reason")
            : Error.Validation("Tickets.Invalid", "Primary reason");
}

/// <summary>Carries no module prefix, so the prefix rule must flag it.</summary>
internal static class UnprefixedErrors
{
    internal static Error Broken() => Error.Validation("SomethingBroke", "No module prefix at all");
}

/// <summary>
/// Builds its code at run time. The code argument is not a literal, so the rule reports the site as
/// UNVERIFIABLE instead of guessing.
/// </summary>
internal static class DynamicErrors
{
    internal static Error For(string entity) => Error.Validation("Tickets." + entity, "Built at run time");
}

/// <summary>Reuses a generic framework static, which the allowed-shared-codes list exempts.</summary>
internal static class SharedCodeErrors
{
    internal static Error Missing() => Error.NotFoundError("Error.NotFound", "Generic shared code");
}
