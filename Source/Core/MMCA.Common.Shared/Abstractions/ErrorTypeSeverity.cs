using System.Collections.Frozen;

namespace MMCA.Common.Shared.Abstractions;

/// <summary>
/// The single severity ranking over <see cref="ErrorType"/>, shared by every transport edge.
/// <para>
/// A <see cref="Result"/> built by <see cref="Result.Combine"/> aggregates errors in evaluation
/// order, so picking the first error to classify the whole failure would let an incidental
/// validation failure downgrade a real 403 or 500 into a 400. Ranking the categories instead
/// makes the classification independent of ordering, and keeping the ranking here (rather than
/// inside one presentation package) makes the HTTP edge and the gRPC edge classify the same
/// aggregate the same way.
/// </para>
/// <para>
/// The ranking, most to least severe:
/// <list type="number">
///   <item><see cref="ErrorType.Unexpected"/>: the server itself is broken.</item>
///   <item><see cref="ErrorType.Unauthorized"/>: the caller has not proven who they are, so nothing else can be judged.</item>
///   <item><see cref="ErrorType.Forbidden"/>: the caller is known but not allowed.</item>
///   <item><see cref="ErrorType.Conflict"/>: the request lost a race with the current state.</item>
///   <item><see cref="ErrorType.NotFound"/>: the target does not exist.</item>
///   <item><see cref="ErrorType.UnprocessableEntity"/>: well-formed but semantically rejected.</item>
///   <item><see cref="ErrorType.Invariant"/>, <see cref="ErrorType.Validation"/>, <see cref="ErrorType.Failure"/>: the caller can fix the request.</item>
/// </list>
/// Equal ranks keep the earliest error, so a list of same-rank errors behaves exactly as a
/// positional selection would.
/// </para>
/// </summary>
public static class ErrorTypeSeverity
{
    /// <summary>
    /// Severity rank per <see cref="ErrorType"/>, highest wins. Uses
    /// <see cref="FrozenDictionary{TKey,TValue}"/> because the table is fixed at startup and read
    /// on every failure path.
    /// </summary>
    private static readonly FrozenDictionary<ErrorType, int> Ranks = new Dictionary<ErrorType, int>
    {
        [ErrorType.Unexpected] = 70,
        [ErrorType.Unauthorized] = 60,
        [ErrorType.Forbidden] = 50,
        [ErrorType.Conflict] = 40,
        [ErrorType.NotFound] = 30,
        [ErrorType.UnprocessableEntity] = 20,
        [ErrorType.Invariant] = 10,
        [ErrorType.Validation] = 10,
        [ErrorType.Failure] = 10,
    }.ToFrozenDictionary();

    /// <summary>
    /// Returns the severity rank of an error type. An unmapped type ranks lowest (zero), so a
    /// category added to <see cref="ErrorType"/> without a rank here can never silently outrank a
    /// real 403 or 500.
    /// </summary>
    /// <param name="errorType">The error category to rank.</param>
    /// <returns>The severity rank; higher is more severe.</returns>
    public static int Rank(ErrorType errorType) => Ranks.GetValueOrDefault(errorType, 0);

    /// <summary>
    /// Selects the representative error for a whole failure: the one whose
    /// <see cref="Error.Type"/> ranks highest per <see cref="Rank(ErrorType)"/>. Ties keep the
    /// earliest error. Transports use this only to classify the failure (HTTP status, gRPC status);
    /// every error still travels in the payload.
    /// </summary>
    /// <param name="errors">The errors carried by the failed result. Must not be empty.</param>
    /// <returns>The most severe error present.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="errors"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="errors"/> is empty.</exception>
    public static Error MostSevere(IReadOnlyList<Error> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            throw new ArgumentException(
                "A failure must carry at least one error to classify.",
                nameof(errors));
        }

        var mostSevere = errors[0];
        var highestRank = Rank(mostSevere.Type);

        // Deliberately index-based: this runs on every failure response, and a LINQ
        // MaxBy would allocate an enumerator plus a comparer closure per call.
        for (var i = 1; i < errors.Count; i++)
        {
            var rank = Rank(errors[i].Type);
            if (rank > highestRank)
            {
                highestRank = rank;
                mostSevere = errors[i];
            }
        }

        return mostSevere;
    }
}
