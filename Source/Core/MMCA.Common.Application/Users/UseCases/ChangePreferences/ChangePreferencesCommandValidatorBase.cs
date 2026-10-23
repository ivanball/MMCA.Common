using FluentValidation;
using MMCA.Common.Domain.Invariants;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Globalization;

namespace MMCA.Common.Application.Users.UseCases.ChangePreferences;

/// <summary>
/// The shared change-preferences validation (ADR-027 culture / ADR-028 theme): the request must be
/// present, a supplied culture must be one the framework supports, and a supplied theme must be light
/// or dark. A <see langword="null"/> field passes, because null is the documented "leave this
/// preference unchanged" value. The app Identity modules carried near-identical copies of this
/// validator.
/// </summary>
/// <remarks>
/// A COMMAND validator rather than a request validator, deliberately: the command carries its payload
/// through <see cref="IUserScopedCommand{TRequest}"/>, which does not opt it into the automatic
/// <c>CommandRequestValidator</c> bridge, so an <c>IValidator&lt;ChangePreferencesRequest&gt;</c>
/// would never run. The class is abstract because module scanning registers validators from the app
/// assemblies only: each app declares a one-line sealed subclass for its own command record, the
/// same split as <see cref="ChangePreferencesHandlerBase{TUser, TCommand}"/>.
/// <para>
/// The accepted values come from the same definitions the aggregate enforces
/// (<see cref="SupportedCultures.IsSupported"/> and
/// <see cref="CommonInvariants.EnsurePreferredThemeIsValid"/>), so this moves the rejection in front
/// of the handler without introducing a second definition of what is valid.
/// </para>
/// </remarks>
/// <typeparam name="TCommand">The app's change-preferences command record.</typeparam>
public abstract class ChangePreferencesCommandValidatorBase<TCommand> : AbstractValidator<TCommand>
    where TCommand : IUserScopedCommand<ChangePreferencesRequest>
{
    /// <summary>Error code for a missing request.</summary>
    private const string PreferencesRequiredCode = "User.Preferences.Required";

    /// <summary>Error code for an unsupported culture.</summary>
    private const string CultureInvalidCode = "User.PreferredCulture.Invalid";

    /// <summary>Error code for a theme other than light or dark.</summary>
    private const string ThemeInvalidCode = "User.PreferredTheme.Invalid";

    /// <summary>Initializes the shared preference rules.</summary>
    protected ChangePreferencesCommandValidatorBase()
    {
        RuleFor(c => c.Request)
            .NotNull().WithMessage("Preferences are required.").WithErrorCode(PreferencesRequiredCode);

        When(c => c.Request is not null, () =>
        {
            RuleFor(c => c.Request.Culture)
                .Must(SupportedCultures.IsSupported)
                    .WithMessage("Culture is not supported.")
                    .WithErrorCode(CultureInvalidCode)
                .When(c => c.Request.Culture is not null);

            RuleFor(c => c.Request.Theme)
                .Must(BeAKnownTheme)
                    .WithMessage($"Theme must be '{CommonInvariants.LightTheme}' or '{CommonInvariants.DarkTheme}'.")
                    .WithErrorCode(ThemeInvalidCode)
                .When(c => c.Request.Theme is not null);
        });
    }

    /// <summary>Delegates to the aggregate's own theme invariant, so the accepted set has one definition.</summary>
    /// <param name="theme">The supplied theme name.</param>
    /// <returns><see langword="true"/> when the aggregate would accept the theme.</returns>
    private static bool BeAKnownTheme(string? theme) =>
        CommonInvariants.EnsurePreferredThemeIsValid(
            theme, ThemeInvalidCode, string.Empty, nameof(ChangePreferencesCommandValidatorBase<>), nameof(ChangePreferencesRequest.Theme)).IsSuccess;
}
