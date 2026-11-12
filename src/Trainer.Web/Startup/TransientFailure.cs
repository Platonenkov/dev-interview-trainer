using System.Net.Sockets;
using Npgsql;

namespace Trainer.Web.Startup;

/// <summary>
/// Tells a failure worth retrying (the database is starting, restarting or unreachable) from one that a retry cannot fix
/// (a missing content directory, a schema mismatch, a bug). EF Core and Npgsql wrap the cause, so the whole chain is checked.
/// </summary>
public static class TransientFailure
{
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true } or TimeoutException or SocketException)
            {
                return true;
            }

            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsTransient))
            {
                return true;
            }
        }

        return false;
    }
}
