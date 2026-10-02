using System.Text.Json;

using BarkCloud.GrpcServer.Metrics;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Services;

/// <summary>
/// Доставляет письма из <c>PendingNotifications</c>: берёт адрес и имя из Users, при необходимости определяет геолокацию
/// и публикует <see cref="EmailNotification"/> в RabbitMQ. Сбой любого шага откладывает строку с растущей паузой
/// до <see cref="MaxAge"/>. Гарантия — как минимум одна доставка: падение между публикацией и удалением строки даёт повтор.
/// Несколько реплик Identity делят очередь через lease (<c>LockToken</c>/<c>LockedUntil</c>).
/// </summary>
public class NotificationOutboxWorker(
    IServiceScopeFactory scopeFactory,
    MetricsCollector metrics,
    ILogger<NotificationOutboxWorker> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan UsersDeadline = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;

            try
            {
                processed = await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка прохода очереди уведомлений Identity");
            }

            // Полная пачка — вероятно, есть ещё готовые строки: следующий проход без паузы.
            if (processed >= BatchSize)
            {
                continue;
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Захватывает готовые строки и обрабатывает каждую. Возвращает число захваченных строк.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(cancellationToken);

        foreach (var notification in claimed)
        {
            try
            {
                await ProcessAsync(notification, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Не удалось даже отложить строку (БД): lease истечёт сам, строка вернётся в очередь.
                logger.LogError(ex, "Не удалось обновить состояние уведомления {NotificationId}", notification.Id);
            }
        }

        return claimed.Count;
    }

    private async Task<List<PendingNotification>> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityContext>();

        var now = DateTime.UtcNow;

        var ids = await context.PendingNotifications
            .AsNoTracking()
            .Where(x => x.NextAttemptAt <= now && (x.LockedUntil == null || x.LockedUntil < now))
            .OrderBy(x => x.NextAttemptAt)
            .Select(x => x.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return [];
        }

        // Условие повторяется в UPDATE: если другая реплика успела взять строку, она останется за ней.
        var token = Guid.NewGuid();
        var lockedUntil = now + Lease;

        await context.PendingNotifications
            .Where(x => ids.Contains(x.Id) && x.NextAttemptAt <= now && (x.LockedUntil == null || x.LockedUntil < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LockToken, token)
                .SetProperty(x => x.LockedUntil, lockedUntil)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);

        return await context.PendingNotifications
            .AsNoTracking()
            .Where(x => x.LockToken == token)
            .ToListAsync(cancellationToken);
    }

    private async Task ProcessAsync(PendingNotification notification, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<IdentityContext>();

        try
        {
            if (notification.CreatedAt < DateTime.UtcNow - MaxAge)
            {
                metrics.Increment("notification_outbox_expired");
                logger.LogWarning(
                    "Уведомление {Type} для пользователя {UserId} не доставлено за {Hours} ч и удалено",
                    notification.Type, notification.UserId, MaxAge.TotalHours);
                await Delete(context, notification.Id, cancellationToken);
                return;
            }

            var usersClient = services.GetRequiredService<UsersServerApi.UsersServerApiClient>();
            var contacts = await usersClient.GetUserContactsAsync(
                new GetUserContactsRequest { UserId = notification.UserId },
                deadline: DateTime.UtcNow + UsersDeadline,
                cancellationToken: cancellationToken);

            var email = contacts.Contact?.Email;

            if (string.IsNullOrEmpty(email))
            {
                metrics.Increment("notification_outbox_dropped");
                logger.LogWarning(
                    "У пользователя {UserId} нет адреса почты: уведомление {Type} не отправляется",
                    notification.UserId, notification.Type);
                await Delete(context, notification.Id, cancellationToken);
                return;
            }

            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(notification.PayloadJson)
                          ?? new Dictionary<string, string>();

            payload["username"] = contacts.User?.Username ?? string.Empty;

            if (payload.TryGetValue("ip", out var ip) && !payload.ContainsKey("location"))
            {
                payload["location"] = await services.GetRequiredService<LocationClient>().GetLocationString(ip);
            }

            await services.GetRequiredService<NotificationQueueSender>().SendNotification(new EmailNotification
            {
                OwnerId = notification.UserId,
                Address = email,
                CreatedAt = notification.CreatedAt,
                Payload = payload,
                ServiceId = ServiceId.Identity,
                Title = notification.Title,
                Type = notification.Type
            });

            await Delete(context, notification.Id, cancellationToken);

            metrics.Increment("notification_outbox_sent");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var retryAt = DateTime.UtcNow + Backoff(notification.Attempts);

            metrics.Increment("notification_outbox_failed");
            logger.LogWarning(ex,
                "Не удалось доставить уведомление {Type} пользователю {UserId} (попытка {Attempt}), следующая — после {RetryAt:u}",
                notification.Type, notification.UserId, notification.Attempts, retryAt);

            await context.PendingNotifications
                .Where(x => x.Id == notification.Id && x.LockToken == notification.LockToken)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.NextAttemptAt, retryAt)
                    .SetProperty(x => x.LockedUntil, (DateTime?)null)
                    .SetProperty(x => x.LockToken, (Guid?)null), CancellationToken.None);
        }
    }

    private static Task<int> Delete(IdentityContext context, Guid id, CancellationToken cancellationToken)
        => context.PendingNotifications.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);

    /// <summary>30 с · 2^(n-1), не больше часа; <paramref name="attempts"/> уже включает текущую попытку.</summary>
    private static TimeSpan Backoff(int attempts)
    {
        var delay = BaseBackoff * Math.Pow(2, Math.Clamp(attempts - 1, 0, 10));

        return delay < MaxBackoff ? delay : MaxBackoff;
    }
}
