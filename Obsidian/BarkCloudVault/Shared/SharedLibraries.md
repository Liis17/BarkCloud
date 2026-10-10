# Shared Libraries

Parent: [[Index]] · See also: [[Backend/GrpcServer]], [[Backend/Identity]], [[Backend/Users]], [[Backend/Notification]]

## Назначение

Обзор общих библиотек `Shared/BarkCloud.Shared.*`: gRPC metadata, идентификаторы JWT, доменные ошибки, RabbitMQ message DTO и оценка сложности пароля.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Shared/BarkCloud.Shared.Auth/` | `MetadataKeys`, `JwtClientInterceptor`, `XAppClientInterceptor`, `XDeviceClientInterceptor`, `XDeviceIdInterceptor`, `XIpClientInterceptor`, `XOsClientInterceptor` | Добавляют token и клиентские metadata в исходящие gRPC-вызовы |
| `Shared/BarkCloud.Shared.Exceptions/` | `BaseGrpcException`, `ExceptionClientInterceptor` | Типизированные доменные ошибки поверх gRPC |
| `Shared/BarkCloud.Shared.Identity/` | `IdentityClaims`, `JwtSecret`, `ServiceId`, `TokenType` | Имена JWT claims, типы токенов/сервисов и получение ключа |
| `Shared/BarkCloud.Shared.Queue/` | `EmailNotification`, `UserDeleted`, `ProcessUploadedFile` | Контракты сообщений MassTransit/RabbitMQ |
| `Shared/BarkCloud.Shared.SecurityUtilities/SecurityUtilities.cs` | `SecurityUtilities` | Оценка сложности пароля и текст/цвет результата |

## Публичные контракты

`MetadataKeys` задаёт имена `x-auth-token`, `x-device-name`, `x-device-id`, `x-os-name`, `x-app-name`, `x-app-version`, `x-ip-address` и `x-real-ip`. `JwtClientInterceptor` добавляет raw token; interceptors устройства, приложения, ОС и IP кодируют значения как Base64 UTF-8. `RequestContextInterceptor` декодирует клиентские значения из gRPC metadata; `x-real-ip` читает `SourceIpResolver` из HTTP-заголовка [[Backend/GrpcServer]].

`IdentityClaims` задаёт JWT claims `x-user-id`, `x-token-type`, `x-service-id`, `x-device-id` и `x-session-id`. `JwtSecret.GetKeyBytes` — общий путь получения UTF-8 ключа. `JwtSecret.MinKeyBytes` задаёт обязательный минимум 32 байта UTF-8 для запуска, подписи/проверки JWT и сохранения секрета в Configuration. Причина — ограничение HS256 signer в IdentityModel: ключ короче 256 бит отклоняется (`IDX10720`), что соответствует RFC 7518 §3.2.

`BaseGrpcException` содержит `ErrorCode`, `ErrorMessage` и metadata. Серверный interceptor переводит доменную ошибку в `FailedPrecondition` с `x-error-code`; `ExceptionClientInterceptor` восстанавливает известный тип ошибки по этому коду.

В `Shared.Queue` сообщения сгруппированы по назначению: уведомления `EmailNotification`, изменения/удаление пользователя `UserChanged*` и `UserDeleted`, обработка завершённой загрузки `ProcessUploadedFile`. Подробности потоков приведены в [[Backend/Users]], [[Backend/Notification]] и [[Backend/IdentityNotificationOutbox]].

## Ограничения и важные детали

`SecurityUtilities` вычисляет score и текстовую/цветовую метку по эвристикам длины, классов символов и уникальности; она не хеширует и не проверяет пароль. Имена ошибок и их коды являются серверным контрактом; каталог исключений не дублируется в этой обзорной заметке.

Комментарий в `JwtSecret` объясняет требование единого UTF-8 представления: иначе не-ASCII секреты дают разные байты при подписи и проверке. Один минимум 32 байта применяется ко всем потребителям ключа.
