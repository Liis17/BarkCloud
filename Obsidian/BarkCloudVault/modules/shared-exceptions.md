# Shared — Exceptions

Parent: [[index]]

## Назначение

Кастомные исключения, наследующиеся от `BaseGrpcException`. Серверные интерсепторы (см. [[modules/backend-grpcserver]] · `ServerExceptionInterceptor.cs`) маппят их в gRPC-статусы; клиентский `ExceptionClientInterceptor` декодирует обратно. Это даёт типизированную обработку доменных ошибок поверх gRPC.

## Расположение

`Shared/BarkCloud.Shared.Exceptions/`

## Файлы

- `BaseGrpcException.cs` — базовый класс
- `Interceptors/ExceptionClientInterceptor.cs` — клиентский интерсептор маппинга

## Домены исключений (фактические)

### Files (2)
- `FileNotFoundException`
- `NotValidFileIdException`

### Identity (22)
`ConfirmationCodeExpiredException`, `ConfirmationCodeIncorrectException`, `ConfirmationCodeNotFoundException`, `EmailExistException`, `InvalidLoginOrPasswordException`, `InvalidOldPasswordException`, `InvalidRefreshTokenException`, `NotSetUsernameOrEmailException`, `NotValidOtpCodeException`, `OtpCodeNeedException`, `OtpNotCreatedException`, `ResetIdExpiredException`, `ResetIdHasIsApprovedException`, `ResetIdNotFoundException`, `SessionNotFoundException`, `UserNotFoundException`, `UsernameExistException`, `UsernameOrEmailIsEmptyException`, `UsernameReservedException`, `XAppInfoIsRequiedException`, `XDeviceNameIsRequiredException`, `XOsNameIsRequiredException`.

### Users (5)
- `BioTooLongException`
- `ChatFolderInvalidNameException`
- `ChatFolderNotFoundException`
- `ProfilePictureHasNotValidType`
- `UserIsDraftException`

> Часть исключений (`BioTooLongException`, `ChatFolder*`) предвосхищают фичи, которые в [[modules/backend-users]] ещё не реализованы.

## Что отсутствует

В текущем коде **нет** доменов: `FastAuth/`, `Messages/`, `Navigator/`. Если/когда появятся (например, при добавлении сервиса чатов или fast-auth flow в Identity) — добавь их сюда.

## Зависимости

- Использует: `Grpc.Core`
- Используется: всеми Backend-микросервисами (бросают), клиентами (получают типизированный exception обратно через interceptor)
