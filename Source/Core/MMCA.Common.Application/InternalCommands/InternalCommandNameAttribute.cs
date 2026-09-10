namespace MMCA.Common.Application.InternalCommands;

/// <summary>
/// Declares a STABLE serialization identity for an <see cref="IInternalCommand"/>, used by the row
/// that carries it in the <c>InternalCommands</c> table.
/// <para>
/// Without it, a scheduled row records the command's CLR assembly-qualified name, so renaming the
/// class, moving it to another namespace, or moving it to another assembly orphans every row already
/// scheduled under the old name. With it, the row records this name, which no refactoring changes.
/// </para>
/// </summary>
/// <remarks>
/// The attribute changes only what NEW rows store. Rows already scheduled under a CLR name keep
/// resolving by that name and stop resolving if it goes away, so applying it to a command whose
/// queue holds pending rows is a two-step move: drain the queue first, then rename. This mirrors
/// <c>EventNameAttribute</c> on the outbox side, and the name must likewise be unique across the
/// commands a host can resolve, because reverse lookup matches on it.
/// <code>
/// [InternalCommandName("Sales.SendOrderReceipt.v1")]
/// public sealed record SendOrderReceipt(int OrderId) : IInternalCommand;
/// </code>
/// </remarks>
/// <param name="name">The stable serialization identity. Must be non-empty and not whitespace.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class InternalCommandNameAttribute(string name) : Attribute
{
    /// <summary>Gets the stable serialization identity stored in place of the CLR type name.</summary>
    public string Name { get; } = Validated(name);

    /// <summary>
    /// Rejects an empty or whitespace name at construction: a blank identity would be stored on
    /// every row of that command and could never be resolved back to a type.
    /// </summary>
    /// <param name="name">The candidate name.</param>
    /// <returns>The validated name.</returns>
    private static string Validated(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }
}
