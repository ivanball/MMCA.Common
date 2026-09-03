using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common.Interfaces;

namespace MMCA.Common.UI.Common;

/// <summary>
/// The page-side half of the Result transport (ADR-030): the four things a Blazor page ever does
/// with a failed <see cref="Result"/>, written once so no page hand-rolls them again.
/// <list type="bullet">
///   <item><see cref="TryGetValue{T}(Result{T}, out T)"/>: unwrap the value, or fall through to the error branch.</item>
///   <item><see cref="OnFailureSetError{T}(Result{T}, Action{string}, IStringLocalizer)"/>: push the message into a page field rendered by an inline alert.</item>
///   <item><see cref="NotifyOnFailure{T}(Result{T}, IToastService, IStringLocalizer, ToastSeverity)"/>: raise the message as a toast.</item>
///   <item><see cref="HasErrorType"/> and friends: branch on WHY it failed (a 401 redirect, a 404 empty state).</item>
/// </list>
/// <para>
/// <b>Localization.</b> Every message is looked up as a resource key with pass-through: a message
/// that matches a key in the supplied <see cref="IStringLocalizer"/> renders translated, and one
/// that does not renders verbatim. That is what lets the same call site handle both an API error
/// whose text the server already localized and a client-side error whose <c>Message</c> is a
/// resource key (ADR-027).
/// </para>
/// <para>
/// <b>Deduplication and order.</b> Messages are made distinct (ordinal) and ordered most severe
/// first via <see cref="ErrorTypeSeverity"/>, so a real 403 or 500 leads and an incidental
/// validation message never buries it. A failure that carries the same message under several codes
/// (a common shape once <c>Result.Combine</c> aggregates invariants) reads as one sentence.
/// </para>
/// </summary>
/// <example>
/// Before (exception-based):
/// <code>
/// try
/// {
///     var dto = await Service.GetByIdAsync(id, cancellationToken: _cts.Token);
///     if (dto is null)
///     {
///         _errorMessage = L["Entity.NotFound"];
///         return;
///     }
///
///     _model = dto;
/// }
/// catch (Exception ex)
/// {
///     Toast.Error(ErrorMessages.LoadError(Title, ex));
/// }
/// </code>
/// After (Result-based):
/// <code>
/// var result = await Service.GetByIdAsync(id, cancellationToken: _cts.Token);
/// if (result.TryGetValue(out var dto))
/// {
///     _model = dto;
/// }
/// else
/// {
///     result.NotifyOnFailure(Toast, L);
/// }
/// </code>
/// </example>
public static class ResultUiExtensions
{
    /// <summary>
    /// Unwraps a successful <see cref="Result{T}"/> inside a conditional, the way
    /// <c>Dictionary.TryGetValue</c> does, so the success and failure branches sit side by side
    /// without a null check that the type system cannot verify.
    /// </summary>
    /// <typeparam name="T">The success value type.</typeparam>
    /// <param name="result">The result to unwrap.</param>
    /// <param name="value">The success value when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the result succeeded and carries a non-null value.</returns>
    /// <example>
    /// <code>
    /// if ((await Service.GetPagedAsync(filters, 1, 10, null, null, cancellationToken: token)).TryGetValue(out var page))
    /// {
    ///     _items = page.Items;
    /// }
    /// </code>
    /// </example>
    public static bool TryGetValue<T>(this Result<T> result, [NotNullWhen(true)] out T? value)
    {
        ArgumentNullException.ThrowIfNull(result);

        // The failure branch is decided by IsFailure, not by the value: for a value type (a
        // (Items, TotalItems) tuple, an int count) the default is never null, so a null test alone
        // would report every failure as a success.
        if (result.IsFailure)
        {
            value = default;
            return false;
        }

        value = result.Value;
        return value is not null;
    }

    /// <summary>
    /// <see cref="TryGetValue{T}(Result{T}, out T)"/> with the errors handed back on the failing
    /// branch, for a page that wants to inspect them rather than only render them.
    /// <para>
    /// One edge is worth knowing: a <em>success</em> that carries a null value also takes the
    /// failing branch, and a success has no errors, so <paramref name="errors"/> comes back empty.
    /// The framework's own services never produce that shape (a 2xx with no value fails with
    /// <c>Http.EmptyResponse</c>), and the renderers here treat an empty list as nothing to say
    /// rather than an empty alert, but a caller that switches on the error list should not assume
    /// it is non-empty.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The success value type.</typeparam>
    /// <param name="result">The result to unwrap.</param>
    /// <param name="value">The success value when this returns <see langword="true"/>.</param>
    /// <param name="errors">The errors when this returns <see langword="false"/>; empty otherwise.</param>
    /// <returns><see langword="true"/> when the result succeeded and carries a non-null value.</returns>
    public static bool TryGetValue<T>(
        this Result<T> result,
        [NotNullWhen(true)] out T? value,
        out IReadOnlyList<Error> errors)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess && result.Value is not null)
        {
            value = result.Value;
            errors = [];
            return true;
        }

        value = default;
        errors = result.Errors;
        return false;
    }

    /// <summary>
    /// The failure's distinct localized messages, most severe first. Empty for a success, so a
    /// caller can bind it straight to a list without a null or success check.
    /// </summary>
    /// <param name="result">The result to read.</param>
    /// <param name="localizer">
    /// Resolves each message as a resource key, passing unknown keys through verbatim.
    /// <see langword="null"/> leaves every message verbatim.
    /// </param>
    /// <returns>The distinct localized messages.</returns>
    public static IReadOnlyList<string> LocalizedErrorMessages(this Result result, IStringLocalizer? localizer = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess)
        {
            return [];
        }

        return [.. result.Errors
            .OrderByDescending(error => ErrorTypeSeverity.Rank(error.Type))
            .Select(error => Localize(error.Message, localizer))
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The whole failure as one sentence: <see cref="LocalizedErrorMessages"/> joined by a space,
    /// which is the single string an inline alert or a snackbar shows.
    /// <see langword="null"/> for a success.
    /// </summary>
    /// <param name="result">The result to read.</param>
    /// <param name="localizer">Resolves each message as a resource key, passing unknown keys through.</param>
    /// <returns>The composed message, or <see langword="null"/> when the result succeeded.</returns>
    public static string? LocalizedErrorMessage(this Result result, IStringLocalizer? localizer = null)
    {
        var messages = result.LocalizedErrorMessages(localizer);
        return messages.Count == 0 ? null : string.Join(" ", messages);
    }

    /// <summary>
    /// Localizes and deduplicates a plain message list: the <c>MudForm.Errors</c> shape, whose
    /// entries are resource keys produced by the model's DataAnnotations.
    /// </summary>
    /// <param name="messages">The raw messages. <see langword="null"/> yields an empty list.</param>
    /// <param name="localizer">Resolves each message as a resource key, passing unknown keys through.</param>
    /// <returns>The distinct localized messages, in their original order.</returns>
    /// <example>
    /// Replaces the hand-rolled <c>_form.Errors.Select(e =&gt; L[e].Value).Distinct(StringComparer.Ordinal)</c>.
    /// </example>
    public static IReadOnlyList<string> LocalizeDistinct(IEnumerable<string>? messages, IStringLocalizer? localizer = null)
    {
        if (messages is null)
        {
            return [];
        }

        return [.. messages
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => Localize(message, localizer))
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Hands the composed failure message to the page's own error field (the one an inline
    /// <c>MudAlert</c> or <see cref="MMCA.Common.UI.Components.PageState.PageErrorState"/> renders), and
    /// clears it on success. Returns the same result so the call can sit inline.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="setError">
    /// Receives the composed message on failure and <see langword="null"/> on success. Typically
    /// <c>message =&gt; _errorMessage = message</c>.
    /// </param>
    /// <param name="localizer">Resolves each message as a resource key, passing unknown keys through.</param>
    /// <returns>The same <paramref name="result"/> instance.</returns>
    /// <example>
    /// Before:
    /// <code>
    /// var response = await AuthService.LoginAsync(request);
    /// if (response is null)
    /// {
    ///     _errorMessage = AuthService.LastError ?? L["Auth.Login.InvalidCredentials"];
    /// }
    /// </code>
    /// After:
    /// <code>
    /// var result = await AuthService.LoginAsync(request);
    /// result.OnFailureSetError(message =&gt; _errorMessage = message, L);
    /// </code>
    /// </example>
    public static Result OnFailureSetError(this Result result, Action<string?> setError, IStringLocalizer? localizer = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(setError);

        setError(result.LocalizedErrorMessage(localizer));
        return result;
    }

    /// <inheritdoc cref="OnFailureSetError(Result, Action{string}, IStringLocalizer)"/>
    /// <typeparam name="T">The success value type.</typeparam>
    public static Result<T> OnFailureSetError<T>(this Result<T> result, Action<string?> setError, IStringLocalizer? localizer = null)
    {
        OnFailureSetError((Result)result, setError, localizer);
        return result;
    }

    /// <summary>
    /// Raises the composed failure message as one toast (never one per error), and does nothing
    /// on success. Returns the same result so the call can sit inline.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="toast">The toast service (see <see cref="IToastService"/>).</param>
    /// <param name="localizer">Resolves each message as a resource key, passing unknown keys through.</param>
    /// <param name="severity">Toast severity; defaults to <see cref="ToastSeverity.Error"/>.</param>
    /// <returns>The same <paramref name="result"/> instance.</returns>
    /// <example>
    /// Before:
    /// <code>
    /// catch (Exception ex)
    /// {
    ///     Toast.Error(ErrorMessages.SaveError(L["Entity.Notification"], ex));
    /// }
    /// </code>
    /// After:
    /// <code>
    /// (await Service.AddAsync(dto, _cts.Token)).NotifyOnFailure(Toast, L);
    /// </code>
    /// </example>
    public static Result NotifyOnFailure(
        this Result result,
        IToastService toast,
        IStringLocalizer? localizer = null,
        ToastSeverity severity = ToastSeverity.Error)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(toast);

        var message = result.LocalizedErrorMessage(localizer);
        if (message is not null)
        {
            toast.Show(message, severity);
        }

        return result;
    }

    /// <inheritdoc cref="NotifyOnFailure(Result, IToastService, IStringLocalizer, ToastSeverity)"/>
    /// <typeparam name="T">The success value type.</typeparam>
    public static Result<T> NotifyOnFailure<T>(
        this Result<T> result,
        IToastService toast,
        IStringLocalizer? localizer = null,
        ToastSeverity severity = ToastSeverity.Error)
    {
        NotifyOnFailure((Result)result, toast, localizer, severity);
        return result;
    }

    /// <summary>
    /// Whether the failure carries at least one error of this category. The category survives the
    /// HTTP round trip (<c>ProblemDetailsResultReader</c>), so a page can branch on the same
    /// <see cref="ErrorType"/> the server produced.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <param name="errorType">The category to look for.</param>
    /// <returns><see langword="true"/> when at least one error has that category.</returns>
    public static bool HasErrorType(this Result result, ErrorType errorType)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsFailure && result.Errors.Any(error => error.Type == errorType);
    }

    /// <summary>
    /// Whether the target did not exist (HTTP 404), the signal a detail page turns into its
    /// "not found" state rather than an error alert.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <returns><see langword="true"/> when the failure is a not-found.</returns>
    public static bool IsNotFound(this Result result) => result.HasErrorType(ErrorType.NotFound);

    /// <summary>
    /// Whether the caller is not (or no longer) authenticated (HTTP 401), the signal a page turns
    /// into a redirect to the login route rather than an error alert.
    /// </summary>
    /// <param name="result">The result to inspect.</param>
    /// <returns><see langword="true"/> when the failure is an authentication failure.</returns>
    public static bool IsUnauthorized(this Result result) => result.HasErrorType(ErrorType.Unauthorized);

    private static string Localize(string message, IStringLocalizer? localizer)
    {
        if (localizer is null || string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        var localized = localizer[message];
        return localized.ResourceNotFound ? message : localized.Value;
    }
}
