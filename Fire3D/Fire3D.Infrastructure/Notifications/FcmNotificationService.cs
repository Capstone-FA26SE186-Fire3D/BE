
using Fire3D.Application.Notifications;
using Fire3D.Infrastructure.Persistence;
using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fire3D.Infrastructure.Notifications;

public sealed class FcmNotificationService(Fire3DDbContext db, ILogger<FcmNotificationService> logger) : INotificationService
{
    public async Task<bool> SendPushAsync(string fcmToken, string title, string body, IDictionary<string, string>? data = null, CancellationToken ct = default)
    {
        try
        {
#pragma warning disable CS0618 // Type or member is obsolete
            var message = new Message
            {
                Token = fcmToken,
#pragma warning restore CS0618 // Type or member is obsolete
                Notification = new Notification
                {
                    Title = title,
                    Body = body
                },
                Data = data == null ? new Dictionary<string, string>() : new Dictionary<string, string>(data)
            };

            var response = await FirebaseMessaging.DefaultInstance.SendAsync(message, ct);
            logger.LogInformation("Successfully sent message: {Response}", response);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending FCM message to token {Token}", fcmToken);
            return false;
        }
    }

    public async Task<int> SendPushToUserAsync(Guid userId, string title, string body, IDictionary<string, string>? data = null, CancellationToken ct = default)
    {
        var tokens = await db.UserDevices
            .Where(x => x.UserId == userId && !string.IsNullOrEmpty(x.FcmToken))
            .Select(x => x.FcmToken!)
            .ToListAsync(ct);

        if (tokens.Count == 0) return 0;

        int successCount = 0;
        foreach (var token in tokens)
        {
            if (await SendPushAsync(token, title, body, data, ct))
            {
                successCount++;
            }
        }
        return successCount;
    }
}

