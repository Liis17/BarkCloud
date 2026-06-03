# BarkCloudKit

Общий сетевой слой BarkCloud для **iOS** и **macOS**-клиентов (единый источник правды).
Извлекается из iOS-таргета (`Ios/BarkCloud/`), на пакет переводятся и iOS, и новые
macOS-таргеты (контейнер-app + FSKit-расширение `BarkCloudFS`).

> **Статус: заготовка Этапа 0.** Здесь пока только *новый* код (поблочное чтение +
> батч-удаление) и манифест. Платформо-независимые файлы из iOS **ещё не перенесены** —
> поэтому пакет **не собирается standalone**, пока не выполнен перенос (см. ниже). Перенос,
> правка `BarkCloud.xcodeproj` и любая сборка/проверка делаются на **Mac (Xcode 16+)** —
> в Linux-окружении этого сделать нельзя.

## Что уже здесь (новое, аддитивное)

- `Sources/BarkCloudKit/RangeBlockReader.swift` — поблочное чтение по HTTP **Range**
  (1 МиБ блоки, дисковый кэш блоков, дедуп параллельных загрузок, откат на скачивание
  целиком при ответе ≠206). Порт `Drive/BarkCloud.Drive.Engine/CloudGateway.cs`.
- `Sources/BarkCloudKit/CloudRepository+BatchDelete.swift` — `batchDeleteFileEntries(_:)`
  через `CloudApi.DeleteFileEntries` (чанки по 100, идемпотентно).
- `scripts/sync_proto.sh` — генерация Swift-стабов из `Shared/BarkCloud.Proto/` в пакет
  (Visibility=Public).
- `Package.swift` — манифест; версии gRPC зеркалят iOS Package.resolved.

## Этап 0 — миграция (на Mac)

1. **Перенести** в `Sources/BarkCloudKit/` платформо-независимые файлы из iOS-таргета:
   - `Networking/`: `GrpcManager.swift` (вкл. `ServerConfig`/`GrpcEndpoint`), `SessionStore.swift`
     (из `Session/`), `FileTransferService.swift`, `InsecureURLSession.swift`,
     `MultipartBodyBuilder.swift`, `Base64Header.swift`, `GrpcError.swift`,
     `AuthErrorCodes.swift`, `CloudErrorCodes.swift`, все `X*Interceptor.swift`, `AuthInterceptor.swift`
   - `Data/Cloud/`: `CloudRepository.swift`, `CloudModels.swift`, `AlbumRepository.swift`
   - `Data/Auth/`: `AuthRepository.swift`, `AuthResult.swift`; `Data/Users/*`
   - `Generated/Proto/*` → лучше перегенерировать через `scripts/sync_proto.sh`.
2. **Сделать `public`** типы и методы, используемые из app/extension (репозитории, сервисы,
   `GrpcManager`, `SessionStore`, `ServerConfig`, модели). В `CloudRepository.swift` изменить
   `private let grpc` → `let grpc` (нужно для `CloudRepository+BatchDelete.swift`).
3. **`XDeviceInterceptor`/`SessionStore`/`ServerConfig`**: добавить macOS-ветки там, где код
   опирался на iOS-only API (`UIDevice` → `Host.current()`/`ProcessInfo`; App Group id и
   keychain-access-group — общие у app и расширения).
4. **Подключить пакет** к `BarkCloud.xcodeproj` как локальную зависимость, удалить
   перенесённые файлы из таргетов (main app, ShareExtension, Widgets), поправить импорты
   (`import BarkCloudKit`).
5. **Проверка:** `swift build`; `xcodebuild` iOS-таргетов и `BarkCloudTests` — зелёные
   (поведение iOS не меняется).

## Тесты (Mac)

Добавить `Tests/BarkCloudKitTests/RangeBlockReaderTests.swift`: локальный HTTP-сервер,
отвечающий `206` с `Content-Range` — проверить сборку файла из блоков и откат на whole
при ответе `200`.
