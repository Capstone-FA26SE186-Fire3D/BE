using Npgsql;
using System.Net.Sockets;

namespace Fire3D.API.Extensions;

/// <summary>Explicit read-only CLI probe; never creates or migrates database objects.</summary>
public static class DatabaseConnectivityCheck
{
    public static async Task<int> RunAsync(IConfiguration configuration, CancellationToken ct)
    {
        var name = configuration["Database:ConnectionName"] ?? "DefaultConnection";
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            Console.Error.WriteLine($"Missing connection string: {name}");
            return 4;
        }
        try
        {
            var options = new NpgsqlConnectionStringBuilder(value)
            {
                Timeout = 10, CommandTimeout = 10, Pooling = false, IncludeErrorDetail = false
            };
            await using var connection = new NpgsqlConnection(options.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("""
                SELECT current_setting('server_version'),
                    COALESCE((SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()), false),
                    to_regclass('public.users') IS NOT NULL,
                    to_regclass('public.organizations') IS NOT NULL,
                    to_regclass('public.auth_refresh_tokens') IS NOT NULL,
                    to_regtype('public.user_role_enum') IS NOT NULL;
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            var authSchema = Enumerable.Range(2, 4).All(reader.GetBoolean);
            Console.WriteLine($"Connected. PostgreSQL {reader.GetString(0)}. Configured client SSL mode: {options.SslMode}. Server-side hop TLS: {reader.GetBoolean(1)} (may differ behind a pooler).");
            Console.WriteLine(authSchema
                ? "Auth schema prerequisites present. Full migration/permissions still require verify.sql."
                : "Auth schema prerequisites missing. Apply the reviewed migration before using auth APIs.");
            return authSchema ? 0 : 2;
        }
        catch (PostgresException error)
        {
            Console.Error.WriteLine($"PostgreSQL rejected the connection/query. SQLSTATE: {error.SqlState}.");
            return 3;
        }
        catch (Exception error) when (error is NpgsqlException or SocketException or TimeoutException or ArgumentException)
        {
            // Do not print connection strings, query parameters or credential-bearing exceptions.
            var cause = error.GetBaseException();
            var detail = cause is SocketException socket ? socket.SocketErrorCode.ToString() : cause.GetType().Name;
            Console.Error.WriteLine($"Database connection failed ({detail}). Check DNS/network, TLS and credentials.");
            return 3;
        }
    }
}