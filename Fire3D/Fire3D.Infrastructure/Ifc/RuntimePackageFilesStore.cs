using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace Fire3D.Infrastructure.Ifc;
/// <summary>BE#110: pinned fet3d.package/1 manifest and files of a started learner session or playtest (runtime_package_files).</summary>
public sealed class RuntimePackageFilesStore(Fire3DDbContext db) : IRuntimePackageFiles
{
    public async Task<AuthResult<JsonElement>> GetAsync(string kind,Guid actor,Guid family,Guid id,CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("SELECT runtime_package_files(@kind,@actor,@family,@id)::text",(NpgsqlConnection)db.Database.GetDbConnection());
        cmd.Parameters.AddWithValue("kind",kind);cmd.Parameters.AddWithValue("actor",actor);cmd.Parameters.AddWithValue("family",family);cmd.Parameters.AddWithValue("id",id);
        using var json=JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);var value=json.RootElement.Clone();
        return value.GetProperty("code").GetString()=="OK" ? AuthResult<JsonElement>.Ok(value)
            : new(default,new(value.GetProperty("code").GetString()!,Message(value.GetProperty("code").GetString()!),value.GetProperty("status").GetInt32()));
    }
    private static string Message(string code)=>code switch
    {
        "PACKAGE_SESSION_NOT_ACTIVE"=>"The package can be downloaded only by the login that started this play, until it completes.",
        "PACKAGE_FORMAT_UNSUPPORTED"=>"This package was not built as fet3d.package/1 and has no downloadable file list.",
        "NOT_FOUND"=>"The session was not found.",
        _=>"The package cannot be read."
    };
}
