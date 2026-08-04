namespace MMCA.Common.Application.Users;

/// <summary>
/// A user-scoped command that carries a request payload.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>ICommandWithRequest&lt;TRequest&gt;</c>: that marker also
/// opts the command into automatic <c>CommandRequestValidator</c> registration, which is a per-app
/// decision (ADC and Store agree on it for the password change and disagree on it for preferences).
/// A command may implement both; implementing this one alone changes no pipeline behavior.
/// </remarks>
/// <typeparam name="TRequest">The embedded request payload type.</typeparam>
public interface IUserScopedCommand<out TRequest> : IUserScopedRequest
{
    /// <summary>The embedded request payload.</summary>
    TRequest Request { get; }
}
