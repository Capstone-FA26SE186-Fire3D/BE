using System.Text.Json;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Fire3D.Infrastructure.Ifc;

public sealed class ProcessingStreamStore(IConfiguration configuration) : IProcessingStreamDispatchGate, IProcessingStreamConsumerGate
{
    Task<JsonElement> IProcessingStreamDispatchGate.ExecuteAsync(string action, JsonElement input, CancellationToken ct) =>
        ExecuteAsync("DispatcherExecutor", "processing_stream_dispatch_gate", action, input, ct);
    Task<JsonElement> IProcessingStreamConsumerGate.ExecuteAsync(string action, JsonElement input, CancellationToken ct) =>
        ExecuteAsync("DefaultConnection", "processing_stream_consumer_gate", action, input, ct);

    private async Task<JsonElement> ExecuteAsync(string connectionName, string function, string action, JsonElement input, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(configuration.GetConnectionString(connectionName));
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand($"SELECT {function}(@action,@input)::text", connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, input.GetRawText());
        using var json = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        return json.RootElement.Clone();
    }
}
