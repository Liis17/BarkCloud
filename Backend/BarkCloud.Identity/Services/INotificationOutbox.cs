using BarkCloud.Shared.Queue.Notifications;

namespace BarkCloud.Identity.Services;

/// <summary>
/// Постановка письма в очередь доставки (outbox Identity). Запись использует тот же контекст и транзакцию,
/// что и основное изменение; письмо доставляет фоновый <see cref="NotificationOutboxWorker"/>.
/// </summary>
public interface INotificationOutbox
{
    /// <summary>
    /// Добавляет письмо в текущую транзакцию и сохраняет его. Возвращает false, если почта выключена;
    /// ошибки записи и сериализации логируются, учитываются метрикой и передаются вызывающей операции.
    /// В <paramref name="payload"/> не нужны <c>username</c> (воркер возьмёт из Users) и <c>location</c>
    /// (воркер определит по <c>ip</c>, если не задана).
    /// </summary>
    Task<bool> EnqueueAsync(long userId, NotificationType type, string title, Dictionary<string, string> payload,
        CancellationToken cancellationToken = default);
}
