
using Fire3D.Application.Notifications;
using Fire3D.Infrastructure.Persistence;
using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fire3D.Infrastructure.Notifications;

public sealed class FcmNotificationService(Fire3DDbContext db, ILogger<FcmNotificationService> logger) : INotificationService
{
    public async Task<bool> SendPushAsync(string fcmToken, string title, string body, IDictionary<string, string>? data = null, CancellationToken ct = default)
        => (await SendCoreAsync(fcmToken, title, body, data, ct)).Delivered;

    private async Task<PushDelivery> SendCoreAsync(string fcmToken, string title, string body, IDictionary<string, string>? data, CancellationToken ct)
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
            return new(true, false);
        }
        catch (FirebaseMessagingException exception) when (exception.MessagingErrorCode == MessagingErrorCode.Unregistered)
        {
            logger.LogWarning("FCM rejected an unregistered token.");
            return new(false, true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FCM delivery failed.");
            return new(false, false);
        }
    }

    public async Task<int> SendPushToUserAsync(Guid userId, string title, string body, IDictionary<string, string>? data = null, CancellationToken ct = default)
    {
        var devices = await db.UserDevices
            .Where(x => x.UserId == userId && x.NotificationsEnabled && x.RevokedAt == null && !string.IsNullOrEmpty(x.FcmToken))
            .Select(x => new { x.Id, Token = x.FcmToken!, x.FcmTokenGeneration })
            .ToListAsync(ct);

        if (devices.Count == 0) return 0;

        int successCount = 0;
        foreach (var device in devices)
        {
            var delivery = await SendCoreAsync(device.Token, title, body, data, ct);
            if (delivery.Delivered)
            {
                successCount++;
            }
            else if (delivery.TokenIsUnregistered)
            {
                await db.UserDevices.Where(x => x.Id == device.Id && x.FcmToken == device.Token && x.FcmTokenGeneration == device.FcmTokenGeneration)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.FcmToken, (string?)null)
                        .SetProperty(x => x.NotificationsEnabled, false)
                        .SetProperty(x => x.RevokedAt, DateTime.UtcNow), ct);
            }
        }
        return successCount;
    }

    private readonly record struct PushDelivery(bool Delivered, bool TokenIsUnregistered);
}

