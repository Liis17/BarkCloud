# Shared — Queue

Parent: [[index]]

## Назначение

DTO-контракты сообщений, которые сервисы публикуют и потребляют через RabbitMQ. Не содержит кода брокера — только сериализуемые типы.

## Расположение

`Shared/BarkCloud.Shared.Queue/`

## События

### Identity
- `SessionRevokedEvent.cs` — отзыв сессии пользователя. Публикуется `Identity` при logout/removeSession; подписаны `Users`, `Files`, `Identity` (см. `SessionRevokedConsumer.cs` в каждом сервисе)

### Users
События об изменении профиля, публикуются `Users`:
- `UserChangedAvatar.cs`
- `UserChangedBio.cs`
- `UserChangedName.cs`
- `UserChangedUsername.cs`
- `UserChangedPassword.cs`

### Notifications
- `Notification.cs` — базовая нотификация
- `EmailNotification.cs` — email-нотификация (используется `Identity` для подтверждений/сбросов через `NotificationQueueSender`)
- `NotificationType.cs` — типы нотификаций
- `TransportId.cs` — идентификатор транспорта (email/push/sms?)

## Поток событий

```
Identity ──► SessionRevokedEvent ──► Users, Files (Consumers/SessionRevokedConsumer)
Identity ──► EmailNotification    ──► (внешний сервис нотификаций, не в этом репо)
Users    ──► UserChanged*         ──► (потребители вне видимости текущего репо)
```

## Зависимости

- Используется: всеми Backend-микросервисами через MassTransit/RabbitMQ.Client (см. их `Consumers/` и `Infrastructure/`-классы вроде `NotificationQueueSender`, `UserInfoQueueSender`)
