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
| `ServiceId.cs` | Идентификаторы Backend-сервисов (используются при запросе настроек у Configuration) |
| `TokenType.cs` | Типы токенов (access, refresh, otp, reset password) |

## Зависимости

- Используется: всеми Backend-микросервисами; `Identity` использует при выпуске токенов, остальные — при их валидации

## Связанные заметки

- [[modules/backend-identity]] — где они применяются при выпуске
- [[modules/backend-grpcserver]] — где они применяются при валидации (XAuth)
