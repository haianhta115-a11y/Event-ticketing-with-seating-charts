using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace EventTicketing.Tests.Infrastructure;

internal sealed class ActivationInsertFailure : DbCommandInterceptor
{
    public bool Triggered { get; private set; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("INSERT INTO account_activations", StringComparison.Ordinal) ||
            command.CommandText.Contains("INSERT INTO \"account_activations\"", StringComparison.Ordinal))
        {
            Triggered = true;
            throw new PostgresException("Simulated activation persistence failure", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation);
        }
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
