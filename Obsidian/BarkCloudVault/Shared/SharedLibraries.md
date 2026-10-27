# Shared Libraries

Parent: [[Index]] · See also: [[Backend/GrpcServer]], [[Backend/Identity]], [[Backend/Users]], [[Backend/Notification]]

## Назначение

Обзор общих библиотек `Shared/BarkCloud.Shared.*`: gRPC metadata, идентификаторы JWT, доменные ошибки, RabbitMQ message DTO и оценка сложности пароля.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Shared/BarkCloud.Shared.Auth/` | `MetadataKeys`, `JwtClientInterceptor`, `XAppClientInterceptor`, `XDeviceClientInterceptor`, `XDeviceIdInterceptor`, `XIpClientInterceptor`, `XOsClientInterceptor` | Добавляют token и клиентские metadata в исходящие gRPC-вызовы |
| `Shared/BarkCloud.Shared.Exceptions/` | `BaseGrpcException`, `ExceptionClientInterceptor` | Типизированные доменные ошибки поверх gRPC |
| `Shared/BarkCloud.Shared.Identity/` | `IdentityClaims`, `RsaJwtKeys`, `ServicePermissions`, `ServiceCredentialRegistry`, `ServiceId`, `TokenType` | JWT claims, RSA-ключи, сервисные идентичности и права |
| `Shared/BarkCloud.Shared.Queue/` | `EmailNotification`, `UserDeleted`, `ProcessUploadedFile` | Контракты сообщений MassTransit/RabbitMQ |
| `Shared/BarkCloud.Shared.SecurityUtilities/SecurityUtilities.cs` | `SecurityUtilities` | Оценка сложности пароля и текст/цвет результата |

## Публичные контракты

`MetadataKeys` задаёт имена `x-auth-token`, `x-device-name`, `x-device-id`, `x-os-name`, `x-app-name`, `x-app-version`, `x-ip-address` и `x-real-ip`. `JwtClientInterceptor` добавляет raw token; interceptors устройства, приложения, ОС и IP кодируют значения как Base64 UTF-8. `RequestContextInterceptor` декодирует клиентские значения из gRPC metadata; `x-real-ip` читает `SourceIpResolver` из HTTP-заголовка [[Backend/GrpcServer]].

`IdentityClaims` задаёт пользовательские и сервисные JWT claims, включая адресата сервисного токена и набор прав. `RsaJwtKeys` загружает PEM-файлы: приватный RSA-ключ подписи требует минимум 3072 бита, публичный ключ проверки — 2048; `kid` вычисляется из публичной части. Подписывает только Identity, валидаторы допускают RS256. Набор публичных файлов через `;` поддерживает перекрытие ключей при ротации. `ServicePermissions` определяет права вызывающего сервиса; `ServiceCredentialRegistry` разбирает реестры отдельных bootstrap/token credentials.

`BaseGrpcException` содержит `ErrorCode`, `ErrorMessage` и metadata. Серверный interceptor переводит доменную ошибку в `FailedPrecondition` с `x-error-code`; `ExceptionClientInterceptor` восстанавливает известный тип ошибки по этому коду.

В `Shared.Queue` сообщения сгруппированы по назначению: уведомления `EmailNotification`, изменения/удаление пользователя `UserChanged*` и `UserDeleted`, обработка завершённой загрузки `ProcessUploadedFile`. Подробности потоков приведены в [[Backend/Users]], [[Backend/Notification]] и [[Backend/IdentityNotificationOutbox]].

Инфраструктурные ACL RabbitMQ не меняют message contracts: `Users`/`Files` сохраняют EF outbox publishers, Identity — `UserDeleted` consumer и `EmailNotification` publisher, Files — `ProcessUploadedFile` и Quartz scheduler commands. Provisioning выдаёт права на routes соответствующей службе; email exchange, Notification base exchange и `Fault<EmailNotification>` доступны только Identity/Notification. См. [[Platform/Infrastructure]].

## Ограничения и важные детали

`SecurityUtilities` вычисляет score и текстовую/цветовую метку по эвристикам длины, классов символов и уникальности; она не хеширует и не проверяет пароль. Имена ошибок и их коды являются серверным контрактом; каталог исключений не дублируется в этой обзорной заметке.

Приватный ключ подписи JWT не передаётся валидаторам; сервисный credential позволяет получить только сервисный токен с правами зарегистрированного вызывающего, а не пользовательский JWT. Срок действия и проверка адресата описаны в [[Backend/GrpcServer]].
