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
- `UserChangedPassword.cs` — DTO существует, но из `Users` не публикуется (пароли — в `Identity`)
- `UserDeleted.cs` — удаление аккаунта. Публикует `Users` при `DeleteAccount`; подписаны `Identity` и `Files` (`UserDeletedConsumer.cs` в каждом)

### Notifications
- `Notification.cs` — базовая нотификация
- `EmailNotification.cs` — email-нотификация (используется `Identity` для подтверждений/сбросов через `NotificationQueueSender`)
- `NotificationType.cs` — типы нотификаций
- `TransportId.cs` — идентификатор транспорта (email/push/sms?)

### Files
- `Files/ProcessUploadedFile.cs` — команда `ProcessUploadedFile { SessionId }` для enrichment завершённого multipart из [[modules/upload-2]]. Публикуется Files в той же EF-транзакции, что переводит `UploadSession` в `processing`, через MassTransit Bus Outbox; consumer `process-uploaded-file` также живёт в Files, concurrency 2, retry 10s/1m/5m/15m

## Поток событий

```
Identity ──► SessionRevokedEvent ──► Users, Files (Consumers/SessionRevokedConsumer)
Identity ──► EmailNotification    ──► (внешний сервис нотификаций, не в этом репо)
Users    ──► UserChanged*         ──► (потребители вне видимости текущего репо)
Users    ──► UserDeleted          ──► Identity, Files (Consumers/UserDeletedConsumer)
Files    ──► ProcessUploadedFile  ──► Files (Consumers/ProcessUploadedFileConsumer)
```

## Зависимости

- Используется: всеми Backend-микросервисами через MassTransit/RabbitMQ.Client (см. их `Consumers/` и `Infrastructure/`-классы вроде `NotificationQueueSender`, `UserInfoQueueSender`). Files дополнительно использует `MassTransit.EntityFrameworkCore` 8.5.2 и PostgreSQL inbox/outbox tables для атомарной доставки Upload 2.0
