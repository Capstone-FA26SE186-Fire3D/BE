using System.Reflection;
using Fire3D.Infrastructure.Notifications;
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class FcmNotificationServiceTests
{
    // FirebaseMessagingException has an internal constructor; build the real SDK exception without any network call.
    private static FirebaseMessagingException Fcm(MessagingErrorCode code)=>(FirebaseMessagingException)Activator.CreateInstance(typeof(FirebaseMessagingException),
        BindingFlags.Instance|BindingFlags.NonPublic,null,new object?[]{ErrorCode.NotFound,"provider detail for token",code,null,null},null)!;
#pragma warning disable CS0618 // Message.Token is the single-device target the service uses.
    private static string Target(Message message)=>message.Token;
#pragma warning restore CS0618

    [BillingPostgresFact]
    public async Task Push_to_user_counts_deliveries_and_revokes_only_the_unregistered_token_generation()
    {
        await using var db=await BillingDatabase.Create(false);
        var user=BillingDatabase.Owner;
        await db.Sql($"""
            INSERT INTO user_devices(user_id,device_uuid,fcm_token,notifications_enabled,fcm_token_generation,revoked_at) VALUES
             ('{user}','phone','fcm-ok',true,1,NULL),
             ('{user}','tablet','fcm-gone',true,3,NULL),
             ('{user}','laptop','fcm-flaky',true,1,NULL),
             ('{user}','rotating','fcm-rotating',true,1,NULL),
             ('{user}','muted','fcm-muted',false,1,NULL),
             ('{user}','logged-out','fcm-revoked',true,1,now()),
             ('{user}','no-token',NULL,true,1,NULL),
             ('{BillingDatabase.Other}','other','fcm-other',true,1,NULL);
            """);
        var sent=new List<Message>();var log=new CapturedLog<FcmNotificationService>();
        await using var context=db.Context();
        var service=new FcmNotificationService(context,log){SendMessage=async (message,_)=>
        {
            sent.Add(message);
            switch(Target(message))
            {
                case "fcm-gone": throw Fcm(MessagingErrorCode.Unregistered);
                case "fcm-flaky": throw Fcm(MessagingErrorCode.Internal);
                case "fcm-rotating":
                    // The device re-registered while FCM rejected its previous token: the new binding must survive.
                    await db.Sql("UPDATE user_devices SET fcm_token='fcm-rotated',fcm_token_generation=2 WHERE device_uuid='rotating'");
                    throw Fcm(MessagingErrorCode.Unregistered);
                default: return "projects/test/messages/1";
            }
        }};
        var data=new Dictionary<string,string>{["type"]="BuildingServiceExpiring"};
        Assert.Equal(1,await service.SendPushToUserAsync(user,"Title","Body",data,default));
        // Only live, enabled bindings of this user are attempted, each with the notification and a copy of the data.
        Assert.Equal(new[]{"fcm-flaky","fcm-gone","fcm-ok","fcm-rotating"},sent.Select(Target).Order().ToArray());
        Assert.All(sent,message=>{Assert.Equal("Title",message.Notification.Title);Assert.Equal("Body",message.Notification.Body);
            Assert.Equal("BuildingServiceExpiring",message.Data["type"]);Assert.NotSame(data,message.Data);});
        Assert.Equal("NULL|false|true|true",await Device(db,"tablet"));
        Assert.Equal("fcm-ok|true|false|false",await Device(db,"phone"));
        Assert.Equal("fcm-flaky|true|false|false",await Device(db,"laptop")); // A provider error is not a revocation.
        Assert.Equal("fcm-rotated|true|false|false",await Device(db,"rotating"));
        Assert.Contains(log.Entries,x=>x.Level==LogLevel.Warning&&x.Message=="FCM rejected an unregistered token.");
        Assert.DoesNotContain("fcm-gone",log.All());
        // No live device: nothing is sent.
        sent.Clear();
        Assert.Equal(0,await service.SendPushToUserAsync(BillingDatabase.Admin,"Title","Body",null,default));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task Single_push_reports_delivery_and_never_throws_provider_errors()
    {
        // A single-token push never touches the database; the context is never opened.
        await using var context=new Fire3D.Infrastructure.Persistence.Fire3DDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Fire3D.Infrastructure.Persistence.Fire3DDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused").Options);
        Message? last=null;Exception? failure=null;var log=new CapturedLog<FcmNotificationService>();
        var service=new FcmNotificationService(context,log){SendMessage=(message,_)=>{last=message;return failure is null?Task.FromResult("id"):Task.FromException<string>(failure);}};
        Assert.True(await service.SendPushAsync("token-a","T","B"));
        Assert.Equal("token-a",Target(last!));Assert.Empty(last!.Data);
        failure=Fcm(MessagingErrorCode.Unregistered);Assert.False(await service.SendPushAsync("token-a","T","B"));
        failure=new HttpRequestException("network");Assert.False(await service.SendPushAsync("token-a","T","B"));
        Assert.Contains(log.Entries,x=>x.Level==LogLevel.Error&&x.Exception is HttpRequestException);
    }

    private static async Task<string> Device(BillingDatabase db,string device)=>(string)(await db.Scalar(
        $"SELECT coalesce(fcm_token,'NULL')||'|'||notifications_enabled||'|'||(revoked_at IS NOT NULL)||'|'||(fcm_token_updated_at IS NOT NULL) FROM user_devices WHERE device_uuid='{device}'"))!;
}
