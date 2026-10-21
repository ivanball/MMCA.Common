using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MMCA.Common.Infrastructure.Tests.TestDoubles;

/// <summary>
/// Records every negative integer parameter a query sends. SQLite reads a negative <c>LIMIT</c> as
/// "no limit" where SQL Server and PostgreSQL reject the negative <c>FETCH</c>/<c>LIMIT</c>, so a
/// wrapped page size is only observable on SQLite by inspecting the parameter itself.
/// </summary>
internal sealed class NegativeParameterRecorder : DbCommandInterceptor
{
    private readonly List<long> _negatives = [];

    /// <summary>Gets the negative integer parameter values seen so far.</summary>
    public IReadOnlyList<long> Negatives => _negatives;

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Record(DbCommand command)
    {
        foreach (DbParameter parameter in command.Parameters)
        {
            long? value = parameter.Value switch
            {
                int number => number,
                long number => number,
                _ => null,
            };

            if (value < 0)
            {
                _negatives.Add(value.Value);
            }
        }
    }
}
