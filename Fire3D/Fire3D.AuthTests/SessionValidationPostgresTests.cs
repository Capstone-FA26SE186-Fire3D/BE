using System.Data.Common;
using Fire3D.API.Extensions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Queries.ValidateSession;
using Fire3D.Infrastructure.Authentication;
using Fire3D.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class SessionValidationPostgresTests
{
    private sealed class Commands : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {Count++;return ValueTask.FromResult(result);}
    }
    [BillingPostgresFact]
    public async Task Session_authorization_uses_one_query_and_rechecks_live_identity_and_family()
    {
        await using var db=await BillingDatabase.Create();var commands=new Commands();
        var services=new ServiceCollection();services.AddLogging();
        services.AddDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:DefaultConnection",db.Connection}}).Build());
        services.ConfigureDbContext<Fire3DDbContext>(options=>options.AddInterceptors(commands));
        services.AddScoped<IAuthStore,AuthStore>();services.AddSingleton(TimeProvider.System);
        services.AddMediatR(options=>options.RegisterServicesFromAssemblyContaining<ValidateSessionQuery>());
        using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();
        var sender=scope.ServiceProvider.GetRequiredService<ISender>();
        async Task Check(bool expected,string role="OrganizationUser",string? tenant=null,Guid? family=null)
        {
            commands.Count=0;
            Assert.Equal(expected,await sender.Send(new ValidateSessionQuery(BillingDatabase.Owner,family??BillingDatabase.Owner,role,tenant??BillingDatabase.Org.ToString())));
            Assert.InRange(commands.Count,0,1);
        }
        await Check(true);
        await Check(false,tenant:BillingDatabase.OtherOrg.ToString());
        await Check(false,role:"PlatformAdmin");await Check(false,role:"0");await Check(false,family:BillingDatabase.Other);
        await db.Sql($"UPDATE organizations SET is_active=false WHERE id='{BillingDatabase.Org}'");await Check(false);
        await db.Sql("UPDATE organizations SET is_active=true; UPDATE users SET registration_expires_at=now()+interval '1 hour',email_verified_at=NULL");await Check(false);
        await db.Sql("UPDATE users SET email_verified_at=now(),is_active=false");await Check(false);
        await db.Sql("UPDATE users SET is_active=true,deleted_at=now()");await Check(false);
        await db.Sql("UPDATE users SET deleted_at=NULL; UPDATE auth_refresh_tokens SET revoked_at=now()");await Check(false);
        await db.Sql("UPDATE auth_refresh_tokens SET revoked_at=NULL,consumed_at=now()");await Check(false);
        await db.Sql("UPDATE auth_refresh_tokens SET consumed_at=NULL,expires_at=now()-interval '1 second'");await Check(false);
    }
}
