# Backend — Notification

Parent: [[index]] · See also: [[modules/backend-identity]] · [[modules/shared-queue]] · [[modules/backend-configuration]]

## Назначение

Сервис уведомлений: потребляет сообщения `EmailNotification` из RabbitMQ и отправляет пользователям письма по SMTP (коды подтверждения регистрации/входа/2FA/сброса пароля, уведомления об успешном и неудачном входе, смене пароля, смене метода 2FA). Внешнего gRPC API нет — это чистый consumer; в nginx не маршрутизируется и наружу портов не открывает. Базы данных у сервиса нет.

## Расположение

`Backend/BarkCloud.Notification/`

## Файлы

### Configurations
- `EmailConfiguration.cs` — SMTP-настройки (`Host`, `Port`, `SenderEmail`, `SenderPassword`); секция `Email`, берётся из [[modules/backend-configuration]]. `AllowInsecure` задаётся только переменной контейнера `SMTP_ALLOW_INSECURE`; `ParseAllowInsecure` принимает `true`/`false` без учёта регистра, отсутствие/пустое значение означает `false`, остальные значения вызывают `FormatException` и останавливают запуск.

### Consumers
- `EmailQueueConsumer.cs` — `IConsumer<EmailNotification>`, слушает очередь `notifications-email-handler`. Метрики `rabbitmq_events_consumed` / `emails_sent` / `emails_failed`. При ошибке пробрасывает исключение → MassTransit retry (письмо не теряется).

### Senders
- `EmailSender.cs` — отправка через MailKit 4.18.1 (`MimeMessage` с HTML-телом). По умолчанию TLS обязателен и сертификат проверяется штатно: порт 465 — `SslOnConnect`, остальные — `StartTls`. При `AllowInsecure=true` проверка отключается только у данного SMTP-клиента; на остальных портах применяется `StartTlsWhenAvailable` (TLS при наличии STARTTLS, иначе plaintext). На 465 TLS остаётся обязательным. Ошибка начатого TLS не вызывает повторную попытку без шифрования. AUTH выполняется после подключения, только если сервер его поддерживает. Ошибки пробрасываются consumer'у. Глобальный `ServicePointManager` не изменяется. `CreateSmtpClient` — protected virtual граница транспорта для тестового CA и перенаправления порта 465 на непривилегированный порт стенда.

### Parsers
- `HtmlEmailTemplateParser.cs` — маппинг `NotificationType → файл шаблона`, подстановка плейсхолдеров `ꟿꟿꟿключꟿꟿꟿ` из `Payload` с HTML-экранированием, авто-добавление `currentyear`.

### Helpers
- `EmailMasker.cs` — маскирование email в логах (`***@domain`).

### Templates (10 HTML)
`confirmation_account`, `confirmation_auth`, `confirmation_otp_email`, `reset_password`, `failed_login`, `successful_registration`, `successful_login`, `password_changed`, `two_factor_method_changed`, `password_changed_by_admin`. Копируются в output (`CopyToOutputDirectory=Always`).

### Корень
- `Program.cs` — читает и проверяет `SMTP_ALLOW_INSECURE` до загрузки удалённой конфигурации; `PostConfigure` фиксирует `EmailConfiguration.AllowInsecure` из окружения, поэтому конфигурация БД не может включить небезопасный режим. При включении один раз после сборки хоста пишет warning. Далее — `LoadConfiguration(ServiceId.Notification)`, Serilog, `SetRunningAddress`, метрики, `AddSettings<EmailConfiguration>("Email")`, MassTransit + RabbitMQ + consumer.
- `appsettings.json`, `Dockerfile`.

## Поток данных

Publisher — [[modules/backend-identity]] (`NotificationQueueSender` → `IPublishEndpoint.Publish(EmailNotification)`). Email-адрес получателя передаётся **прямо в сообщении** (`EmailNotification.Address`), поэтому Notification не обращается к Users. Контракты — в [[modules/shared-queue]] (`Notifications/`).

> **Режим без почты:** при пустых `Email:*` в [[modules/backend-configuration]] сервер выставляет `Features:EmailEnabled=false`; Identity тогда **не публикует** `EmailNotification` (guard в `NotificationQueueSender`), очередь не наполняется. Сервис Notification в этом случае **опционален** — его можно остановить или убрать из docker-compose (никто не `depends_on`). В разделе «Обслуживание» веба показывается соответствующая пометка ([[modules/web-system-updates]]).

## Зависимости

- Использует: `BarkCloud.GrpcServer`, `BarkCloud.Shared.Queue`, `MassTransit.RabbitMQ`, `MailKit` 4.18.1 (MimeKit транзитивно)
- ServiceId: `Notification = 3` ([[modules/shared-identity]])

## Окружение

`ASPNETCORE_ENVIRONMENT`, `CONFIGURATION_SERVICE_URL`. Из [[modules/backend-configuration]] приходят: `RunSettings:Port` (дефолт 7022), общие `RabbitMQ:*` и `Seq` (ServiceId.Unknown), а также `Email:*` для `ServiceId.Notification`. SMTP-настройки (`Email:*` — `Host`, `Port`, `SenderEmail`, `SenderPassword`) — секреты: Configuration при каждом старте досевает недостающие ключи в БД пустыми (идемпотентно, без дубликатов — см. `EnsureSeedAsync` в [[modules/backend-configuration]]), реальные значения вписываются в БД configuration вручную.

`SMTP_ALLOW_INSECURE=false` — безопасный режим по умолчанию. Для собственного SMTP с недоверенным сертификатом или без TLS оператор явно задаёт `SMTP_ALLOW_INSECURE=true` в `.env` либо `docker run -e SMTP_ALLOW_INSECURE=true`. Compose передаёт переменную в `cloud-notification` с дефолтом `false`; после изменения `.env` контейнер нужно пересоздать (`docker compose up -d --force-recreate cloud-notification`). Без TLS пароль и письмо передаются открыто; при отключённой проверке сертификата возможен MITM. Для собственного CA безопасная альтернатива — добавить его в доверие контейнера и оставить флаг выключенным.

## Проверка SMTP

`Tests/Backend/BarkCloud.Notification.Tests/Senders/EmailSenderTests.cs` проверяет реальное SMTP/TLS-соединение с локальным `LocalSmtpServer`: доверенный CA, самоподписанный сертификат, неверный hostname, истёкший срок, plaintext, implicit TLS, сломанный handshake, отсутствие AUTH, сохранение HTML/адресов/темы и изоляцию небезопасного клиента. Тестовый CA задаётся только TLS-политике тестового клиента; системное доверие не меняется. `Configurations/EmailConfigurationTests.cs` проверяет парсинг флага и запуск отдельного процесса с некорректным окружением. См. [[structure/testing]].
