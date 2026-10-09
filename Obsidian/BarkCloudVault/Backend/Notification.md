# Backend — Notification

Parent: [[Index]] · See also: [[Backend/Identity]], [[Backend/IdentityNotificationOutbox]], [[Backend/Configuration]], [[Shared/SharedLibraries]]

## Назначение

Consumer RabbitMQ, который принимает `EmailNotification` и отправляет HTML-письма через SMTP. Внешнего gRPC API и собственной предметной БД нет.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.Notification/Program.cs` | `Program` | Конфигурация consumer и очереди |
| `Backend/BarkCloud.Notification/Consumers/EmailQueueConsumer.cs` | `EmailQueueConsumer` | Обработка сообщения |
| `Backend/BarkCloud.Notification/Senders/EmailSender.cs` | `EmailSender` | Подключение SMTP и отправка |
| `Backend/BarkCloud.Notification/Parsers/HtmlEmailTemplateParser.cs` | `HtmlEmailTemplateParser` | Выбор шаблона и подстановка payload |
| `Backend/BarkCloud.Notification/Configurations/EmailConfiguration.cs` | `EmailConfiguration` | SMTP-параметры |
| `Shared/BarkCloud.Shared.Queue/Notifications/EmailNotification.cs` | `EmailNotification` | Сообщение с адресом, темой, типом и payload |

## Публичные контракты

Consumer слушает RabbitMQ endpoint `notifications-email-handler`. `EmailNotification` содержит получателя `Address`, заголовок `Title`, тип и словарь payload; Identity передаёт сообщение по контракту из [[Shared/SharedLibraries]].

Шаблоны выбираются по `NotificationType`. Плейсхолдеры вида `ꟿꟿꟿkeyꟿꟿꟿ` заменяются значениями из payload с HTML-экранированием; отсутствующий ключ остаётся плейсхолдером. После ошибки отправки consumer пробрасывает исключение в pipeline MassTransit.

## Зависимости и взаимодействия

`Email:Host`, `Email:Port`, `Email:SenderEmail`, `Email:SenderPassword` и `Email:AllowInsecure` поступают из [[Backend/Configuration]]. Identity публикует письма в RabbitMQ; для отложенной доставки используется [[Backend/IdentityNotificationOutbox]].

## Ограничения и важные детали

Порт 465 использует implicit TLS. На остальных портах по умолчанию требуется STARTTLS с проверкой сертификата. При `Email:AllowInsecure=true` проверка сертификата отключается для этого SMTP-клиента, а STARTTLS становится необязательным; на порту 465 TLS остаётся обязательным. Начальное значение настройки — `false`.
