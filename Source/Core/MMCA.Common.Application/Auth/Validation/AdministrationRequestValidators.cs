using FluentValidation;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth.Requests;

namespace MMCA.Common.Application.Auth.Validation;

/// <summary>
/// Validates a role assignment. The names are checked for shape only; whether a role exists is the
/// administration service's decision, because the set of roles is app-owned.
/// </summary>
public class SetUserRolesRequestValidator : AbstractValidator<SetUserRolesRequest>
{
    /// <summary>Initializes the validator.</summary>
    public SetUserRolesRequestValidator()
    {
        // An empty list is legal: it is how an operator strips every role from an account. What is
        // rejected is a null list, which is a malformed body rather than an intent.
        RuleFor(x => x.Roles).NotNull().WithMessage("A role list is required.");

        RuleForEach(x => x.Roles)
            .NotEmpty().WithMessage("A role name cannot be empty.")
            .MaximumLength(PermissionGrant.RoleMaxLength)
                .WithMessage($"A role name cannot exceed {PermissionGrant.RoleMaxLength} characters.");
    }
}

/// <summary>
/// Validates a stored-permission assignment for one role.
/// </summary>
public class SetRolePermissionsRequestValidator : AbstractValidator<SetRolePermissionsRequest>
{
    /// <summary>Initializes the validator.</summary>
    public SetRolePermissionsRequestValidator()
    {
        // Empty means "this role gets no STORED permissions", which is how an operator undoes every
        // grant. It never removes what the host compiled in.
        RuleFor(x => x.Permissions).NotNull().WithMessage("A permission list is required.");

        RuleForEach(x => x.Permissions)
            .NotEmpty().WithMessage("A permission name cannot be empty.")
            .MaximumLength(PermissionGrant.PermissionMaxLength)
                .WithMessage($"A permission name cannot exceed {PermissionGrant.PermissionMaxLength} characters.");
    }
}
