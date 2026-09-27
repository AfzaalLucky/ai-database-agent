using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace AiDatabaseAgent.Infrastructure.SqlServer;

/// <summary>
/// An open connection whose security context has been switched to the agent's database user with
/// <c>EXECUTE AS USER ... WITH COOKIE</c>. The cookie stays in this process — SQL run on the
/// session cannot REVERT without it — and disposal reverts with the cookie. If the revert fails for
/// any reason, the connection's pool is cleared so an impersonated connection is never reused.
/// <para>
/// Both statements are sent as plain ad hoc batches, not parameterised commands: SQL Server only
/// accepts <c>WITH COOKIE</c> at the ad hoc level (a parameterised command runs inside sp_executesql
/// and fails with error 15590). The user name comes from configuration and is validated as a simple
/// identifier; the cookie is embedded as a binary literal.
/// </para>
/// </summary>
internal sealed partial class ImpersonatedSession : IAsyncDisposable
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleIdentifier();

    private readonly byte[] _cookie;

    private ImpersonatedSession(SqlConnection connection, byte[] cookie)
    {
        Connection = connection;
        _cookie = cookie;
    }

    public SqlConnection Connection { get; }

    public static async Task<ImpersonatedSession> OpenAsync(string connectionString, string agentUser, CancellationToken ct)
    {
        if (!SimpleIdentifier().IsMatch(agentUser))
            throw new ArgumentException($"Agent user '{agentUser}' must be a simple identifier (letters, digits, underscore).", nameof(agentUser));

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                DECLARE @cookie varbinary(8000);
                EXECUTE AS USER = N'{agentUser}' WITH COOKIE INTO @cookie;
                SELECT @cookie;
                """;
            var cookie = await cmd.ExecuteScalarAsync(ct) as byte[]
                ?? throw new InvalidOperationException("EXECUTE AS did not return a cookie.");
            return new ImpersonatedSession(connection, cookie);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public SqlCommand CreateCommand(string sql, int timeoutSeconds)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeoutSeconds;
        return cmd;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Connection.State == ConnectionState.Open)
            {
                await using var cmd = Connection.CreateCommand();
                cmd.CommandText = $"REVERT WITH COOKIE = 0x{Convert.ToHexString(_cookie)};";
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Never hand a still-impersonated connection back to the pool.
            SqlConnection.ClearPool(Connection);
        }
        finally
        {
            await Connection.DisposeAsync();
        }
    }
}
