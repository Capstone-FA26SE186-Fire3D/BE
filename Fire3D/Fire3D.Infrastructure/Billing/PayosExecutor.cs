using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
namespace Fire3D.Infrastructure.Billing;
/// <summary>Dedicated login identities; financial DML is only inside audited SECURITY DEFINER gates.</summary>
public sealed class PayosExecutor(IConfiguration configuration)
{
    public async Task<Guid?> Run(bool webhook,string sql,CancellationToken ct,params (string Name,object Value)[] args)
    {
        var role=webhook?"fet3d_payos_webhook_executor":"fet3d_payos_request_executor";
        var value=configuration.GetConnectionString(webhook?"PayosWebhookExecutor":"PayosRequestExecutor")
            ??throw new InvalidOperationException("PayOS executor connection is not configured.");
        var settings=new NpgsqlConnectionStringBuilder(value);
        var primary=new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("DefaultConnection"));
        if(settings.Host!=primary.Host||settings.Port!=primary.Port||settings.Database!=primary.Database)
            throw new InvalidOperationException("PayOS executor must target the application database.");
        await using var connection=new NpgsqlConnection(value);await connection.OpenAsync(ct);
        await using(var check=new NpgsqlCommand("""
            SELECT NOT (rolsuper OR rolbypassrls OR rolcreaterole OR rolcreatedb)
             AND pg_has_role(current_user,@role,'MEMBER')
             AND NOT pg_has_role(current_user,'fet3d_payos_ledger_owner','MEMBER')
             AND NOT has_table_privilege(current_user,'public.payos_payment_requests','INSERT,UPDATE,DELETE')
             AND NOT has_table_privilege(current_user,'public.payment_transactions','INSERT,UPDATE,DELETE')
            FROM pg_roles WHERE rolname=current_user
            """,connection))
        {
            check.Parameters.AddWithValue("role",role);
            if(!Equals(await check.ExecuteScalarAsync(ct),true))throw new InvalidOperationException("Unsafe PayOS executor privileges.");
        }
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await new NpgsqlCommand("SET LOCAL ROLE "+role,connection,transaction).ExecuteNonQueryAsync(ct);
        await using var command=new NpgsqlCommand(sql,connection,transaction){CommandTimeout=20};
        foreach(var (name,input) in args)command.Parameters.AddWithValue(name,input);
        var result=await command.ExecuteScalarAsync(ct);await transaction.CommitAsync(ct);
        return result is Guid id?id:null;
    }
}
