# Backend — Notification

Parent: [[index]] · See also: [[modules/backend-identity]] · [[modules/shared-queue]] · [[modules/backend-configuration]]

## Назначение

Сервис уведомлений: потребляет сообщения `EmailNotification` из RabbitMQ и отправляет пользователям письма по SMTP (коды подтверждения регистрации/входа/2FA/сброса пароля, уведомления об успешном и неудачном входе, смене пароля, смене метода 2FA). Внешнего gRPC API нет — это чистый consumer; в nginx не маршрутизируется и наружу портов не открывает. Базы данных у сервиса нет.

## Расположение

`Backend/BarkCloud.Notification/`

## Файлы

### Configurations
- `EmailConfiguration.cs` — SMTP-настройки (`Host`, `Port`, `SenderEmail`, `SenderPassword`); секция `Email`, берётся из [[modules/backend-configuration]].

### Consumers
- `EmailQueueConsumer.cs` — `IConsumer<EmailNotification>`, слушает очередь `notifications-email-handler`. Метрики `rabbitmq_events_consumed` / `emails_sent` / `emails_failed`. При ошибке пробрасывает исключение → MassTransit retry (письмо не теряется).

### Senders
- `EmailSender.cs` — отправка через `System.Net.Mail.SmtpClient` (EnableSsl, NetworkCredential). Тело письма формирует парсер по типу уведомления.

### Parsers
- `HtmlEmailTemplateParser.cs` — маппинг `NotificationType → файл шаблона`, подстановка плейсхолдеров `ꟿꟿꟿключꟿꟿꟿ` из `Payload` с HTML-экранированием, авто-добавление `currentyear`.

### Helpers
- `EmailMasker.cs` — маскирование email в логах (`***@domain`).

### Templates (10 HTML)
`confirmation_account`, `confirmation_auth`, `confirmation_otp_email`, `reset_password`, `failed_login`, `successful_registration`, `successful_login`, `password_changed`, `two_factor_method_changed`, `password_changed_by_admin`. Копируются в output (`CopyToOutputDirectory=Always`).

### Корень
- `Program.cs` — `LoadConfiguration(ServiceId.Notification)`, Serilog, `SetRunningAddress`, метрики, `AddSettings<EmailConfiguration>("Email")`, MassTransit + RabbitMQ + consumer.
- `appsettings.json`, `Dockerfile`.

## Поток данных

Publisher — [[modules/backend-identity]] (`NotificationQueueSender` → `IPublishEndpoint.Publish(EmailNotification)`). Email-адрес получателя передаётся **прямо в сообщении** (`EmailNotification.Address`), поэтому Notification не обращается к Users. Контракты — в [[modules/shared-queue]] (`Notifications/`).

## Зависимости

- Использует: `BarkCloud.GrpcServer`, `BarkCloud.Shared.Queue`, `MassTransit.RabbitMQ`
- ServiceId: `Notification = 3` ([[modules/shared-identity]])

## Окружение

`ASPNETCORE_ENVIRONMENT`, `CONFIGURATION_SERVICE_URL`. Из [[modules/backend-configuration]] приходят: `RunSettings:Port` (дефолт 7022), общие `RabbitMQ:*` и `Seq` (ServiceId.Unknown), а также `Email:*` для `ServiceId.Notification`. SMTP-настройки (`Email:*` — `Host`, `Port`, `SenderEmail`, `SenderPassword`) — секреты: Configuration при каждом старте досевает недостающие ключи в БД пустыми (идемпотентно, без дубликатов — см. `EnsureSeedAsync` в [[modules/backend-configuration]]), реальные значения вписываются в БД configuration вручную.
