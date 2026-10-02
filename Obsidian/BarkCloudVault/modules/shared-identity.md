# Shared — Identity

Parent: [[index]]

## Назначение

Маленькая библиотека с константами/enum'ами для идентификации, используемая всеми сервисами для согласованности claim'ов JWT, идентификаторов сервисов и типов токенов.

## Расположение

`Shared/BarkCloud.Shared.Identity/`

## Файлы

| Файл | Назначение |
|------|-----------|
| `IdentityClaims.cs` | Имена claim'ов в JWT (user_id, device_id, и т.д.) |
| `JwtSecret.cs` | Единственный способ получить байты ключа из `JwtSettings:SecretKey` (F23): `GetKeyBytes(secret, minBytes)` — UTF-8, пустой/короткий секрет → `InvalidOperationException` с русским сообщением без значения секрета. `MinStartupBytes = 16` (предел HS256 в IdentityModel, проверка при старте сервисов), `RecommendedMinBytes = 32` (RFC 7518, проверка при записи в Configuration). Длина считается в байтах, не в символах |
| `ServiceId.cs` | Идентификаторы Backend-сервисов (используются при запросе настроек у Configuration) |
| `TokenType.cs` | Типы токенов (access, refresh, otp, reset password) |

## Зависимости

- Используется: всеми Backend-микросервисами; `Identity` использует при выпуске токенов, остальные — при их валидации

## Секрет JWT (F23)

Раньше Identity подписывал по UTF-8, а GrpcServer, Web и Configuration брали ASCII-байты секрета: `Encoding.ASCII` заменяет не-ASCII символы на `?`, поэтому при кириллическом секрете токены Identity не проходили проверку, а сам ключ вырождался в строку «?». Теперь все шесть мест берут байты из `JwtSecret.GetKeyBytes` (подпись: `JwtService`, Web `ServiceToken`, Configuration `GenerateServiceToken`; проверка: `XAuthExtensions`, Web `AuthGateway`; HMAC админ-cookie: Web `AdminGate`). Для ASCII-секретов байты те же, что и раньше. При не-ASCII секрете сохранённые `*Service:Token` (подписаны по старому ASCII-ключу) нужно очистить в Configuration и перезапустить Configuration. См. [[modules/backend-audit]].

## Связанные заметки

- [[modules/backend-identity]] — где они применяются при выпуске
- [[modules/backend-grpcserver]] — где они применяются при валидации (XAuth)
