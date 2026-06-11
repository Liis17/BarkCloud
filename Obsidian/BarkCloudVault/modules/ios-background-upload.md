# iOS — Background Upload + Live Activity

Parent: [[ios-app]]

## Контекст

До этой работы:
- Все загрузки (Share Extension, BackupManager, ручные из Cloud Browser) шли через
  `URLSessionConfiguration.default` (`Networking/InsecureURLSession.swift`) — обычная
  переднеплановая сессия. При сворачивании iOS убивал приложение через ~30 с, задачи
  обрывались.
- Share Extension только складывал файлы в App Group container
  (`<group>/ShareInbox/<uuid>/<name>`) и показывал плашку «Загрузим при следующем
  открытии BarkCloud». Реальная загрузка стартовала когда пользователь открывал main app.
- Не было визуального индикатора, что загрузка идёт после сворачивания.

Цель: загрузка переживает kill main app; Share Extension стартует upload прямо в
момент шеринга; пользователь видит прогресс в Lock Screen / Dynamic Island.

## Архитектура

### Background URLSession (singleton координатор)

`Networking/BackgroundUploadCoordinator.swift` — `final class : NSObject : @unchecked Sendable`.

```swift
let config = URLSessionConfiguration.background(withIdentifier: "com.barkfluff.BarkCloud.upload")
config.sharedContainerIdentifier = "group.com.barkfluff.BarkCloud"
config.sessionSendsLaunchEvents = true
```

Один и тот же `identifier` используется в main app и в Share Extension — iOS-демон
объединяет очереди. Background URLSession принимает только файл
(`uploadTask(with:fromFile:)`), не Data.

Координатор является делегатом сессии:
- `urlSession(_:didReceive challenge:)` — самоподписанный TLS (зеркалит
  `SelfSignedTrustDelegate` из `InsecureURLSession.swift`).
- `urlSessionDidFinishEvents(forBackgroundURLSession:)` — зовёт сохранённый
  background completion handler.
- `urlSession(_:task:didSendBodyData:)` — обновляет `UploadJob.bytesSent` →
  `UploadLiveActivityController.notifyChanged()`.
- `urlSession(_:task:didCompleteWithError:)` — `completed`/`failed`, парсит
  `fileId` из JSON-ответа, удаляет multipart body файл.
- `urlSession(_:dataTask:didReceive data:)` — накапливает body для парсинга
  `fileId` (через `NSLock`-защищённый словарь `[taskIdentifier: Data]`).

Внешние хуки (для UI и BGTask):
- `tokenProvider: @Sendable () async -> String?` — main app передаёт
  `transfer.validAccessToken`, Share Extension — свой `FileTransferService`.
- `onJobProgress`, `onJobCompleted`, `onJobFailed: @MainActor (UploadJobSnapshot) -> Void`.
- `onPersistentFailure: @MainActor () -> Void` — в main app устанавливается
  на `scheduleRetryBGTaskIfNeeded`; в Share Extension nil (extension не имеет
  доступа к BackgroundTasks-фреймворку).

### Persist-очередь

`Data/Cache/UploadJob.swift` — SwiftData `@Model`. Поля: id, sourceKind
(`manual|share|backup`), sourceFilePath, multipartBodyPath, fileName, mimeType,
directoryID, uploadURL, preparedFileID, stateRaw (`pending|preparing|running|completed|failed`),
bytesSent, totalBytes, sessionTaskIdentifier, retries, lastError, createdAt, updatedAt.

`Data/Cache/UploadQueueStore.swift` — actor поверх SwiftData. Контейнер живёт
в App Group: `<group>/UploadQueue.sqlite` — доступен и main app, и Share Extension.
CRUD + `recentJobs(since:)` для Live Activity + `failedJobs(maxRetries:)` для BGTask
+ `activeJobs()` для re-attaching.

Все методы возвращают **Sendable snapshot** (`UploadJobSnapshot`), а не сам
`UploadJob` — чтобы actor-bounded модель не утекала на другие исполнители.

### MultipartBodyBuilder

`Networking/MultipartBodyBuilder.swift` — собирает multipart body как файл
**стримом**, без `Data` в RAM:

```swift
static func writeMultipartFile(
    boundary: String, fieldName: String = "file",
    fileName: String, mimeType: String,
    sourceFile: URL, destination: URL
) throws -> Int64
```

Header → байты оригинала чанками по 64 KB → footer. Возвращает суммарный размер.

### Live Activity

`Shared/UploadActivityAttributes.swift` — `ActivityAttributes` (membership:
main app + ShareExtension + BarkCloudWidgets). ContentState:
- `totalFiles, completedFiles, failedFiles: Int`
- `currentFileName: String`
- `currentProgress, overallProgress: Double` (0…1)
- `isFinished: Bool`

`Networking/UploadLiveActivityController.swift` (`@MainActor`) — singleton,
поднимает / обновляет / завершает одну агрегированную Activity на сессию.
Сессия начинается с первого job в пустой очереди, заканчивается через 3 с
после того как все job ушли в `completed`/`failed` (`dismissalPolicy: .after`).

Прогресс пересчитывается из `UploadQueueStore.recentJobs(since: sessionStart)`:
- `totalFiles` = count(jobs)
- `completedFiles` = count(state == .completed)
- `failedFiles` = count(state == .failed)
- `currentFileName` = первый job state == `.running` или `.preparing`
- `overallProgress` = sum(bytesSent or totalBytes for completed) / sum(totalBytes)

Координатор зовёт `notifyChanged()` после каждого события (submit / progress /
completed / failed). Если `ActivityAuthorizationInfo().areActivitiesEnabled == false` —
Activity не стартуется, координатор продолжает работать.

`BarkCloudWidgets/UploadLiveActivity.swift` — SwiftUI Widget с анимациями:
- **Lock Screen / banner**: радиальный градиент за иконкой облака (.pulse
  symbolEffect), счётчик с `contentTransition(.numericText())`, прогресс-бар
  с шиммером (`ShimmerProgressBar` через TimelineView).
- **Dynamic Island**:
  - leading (compact): пульсирующая иконка облака в оранжевом.
  - trailing (compact): `CompactRing` — кольцо прогресса + счётчик внутри.
  - expanded: иконка в круге + центр (имя файла) + trailing (счётчик
    с numericText) + bottom (ShimmerProgressBar).
  - minimal: `BreathingRing` — кольцо прогресса с дыхательной анимацией
    (scale 0.92↔1.0).
- При завершении иконка меняется на `checkmark.icloud.fill` (успех) или
  `exclamationmark.icloud.fill` (если были ошибки), pulse-эффект гасится.

### Глобальный баннер прогресса в приложении

`Networking/UploadProgressObserver.swift` (`@MainActor @Observable`) — параллельно
с `UploadLiveActivityController` подписан на координатор через `addObserver`.
Хранит агрегаты текущей сессии (totalFiles, completedFiles, currentFileName,
overallProgress, currentSource) для UI. Прогресс-события дебаунсятся ~100 ms,
completion/failure — пересчитываются сразу.

`Features/Main/GlobalUploadBanner.swift` — плавающая плашка с .regularMaterial
фоном, видна на любой вкладке через `.overlay(alignment: .bottom)` в MainScreen.
Скрывается через 1.5 с после `isFinished`. По тапу при `currentSource == .backup`
открывается таб «Галерея» (там пользователь может открыть BackupSheet).

`addObserver` в `BackgroundUploadCoordinator` принимает несколько слушателей
(массив progressListeners/completionListeners/failureListeners под `lock`).
AppEnvironment подписывает системный хук attachFile, UploadProgressObserver —
свой UI-наблюдатель.

### Share Extension: выбор папки

`ShareExtension/ShareViewController.swift` — UIKit-based, показывает UI
выбора целевой папки до загрузки:
1. После `viewDidLoad` → `prepare()` копирует attachments в App Group container.
2. Дёргает `grpc.cloudStub().listDirectoryDetailed("")` чтобы узнать папки
   корня; ищет/создаёт «Недавно загруженные» как default.
3. Показывает stack: иконка/заголовок, имя файла(ов), чип-кнопка «Папка: …»
   с `UIMenu` (через `showsMenuAsPrimaryAction = true`), кнопки «Загрузить»
   и «Отмена». Без сети fallback: список папок пустой, выбранная папка сбрасывается в nil
   (отображается как «Без папки», файл уходит в корень).
4. По «Загрузить» — `enqueue` для каждого: getUploadURL, multipart body в App
   Group, UploadJob с `directoryID = selectedFolder?.id`, submit.
5. main app позже (после восстановления через `handleEventsForBackgroundURLSession`)
   получит completion-событие и сам сделает `attachFile` через observer
   AppEnvironment.

### Auto-refresh галереи при возврате на таб

`Features/Main/MainScreen.swift` — `.onChange(of: selection)`: когда выбран
`.gallery`, вызывает `env.backupManager.refreshScanForNewAssets()`. Это
повторно проходит по `PHAsset.fetchAssets(with:)`, пропуская уже виденные
ассеты (`processedAssetIDs: Set<String>` в BackupManager). Если есть автозагрузка
и в `pendingUpload` появились новые — `uploadLoop` сам поднимется через `classify`.

⚠️ `startScanIfNeeded` теперь обнуляет `scanTask = nil` по завершении скана — иначе
`refreshScanForNewAssets` (guard `scanTask == nil`) никогда не запускался повторно
после первого скана, и новые фото/возобновление не подхватывались.

### Возобновление автозагрузки при возврате в foreground

`BackupManager.resumeOnForeground()` (зовётся из `BarkCloudApp.scenePhase == .active`
вместо прежнего `refreshScanForNewAssets`). Чинит «зависшую» автозагрузку после
сворачивания: цикл подачи мог завершиться (вся очередь подана в URLSession), а часть
фоновых передач — прерваться, и их ассеты остаются в `processedAssetIDs`, так что
обычный повторный скан их пропустит.
1. `startUploadLoop()` — оживляет цикл, если он умер, а в памяти ещё есть `pendingUpload`.
2. Если backup-передач в очереди сейчас НЕТ (загрузка действительно встала) —
   пере-сверка: `processedAssetIDs = confirmedInCloudIDs ∪ {pendingUpload} ∪ {currentAsset}`,
   т.е. из «обработанных» выкидываем всё, что ещё НЕ подтверждено на сервере, и
   `refreshScanForNewAssets` пере-проверяет это по `checkFileHashes` → недостающее
   уходит в загрузку снова (бэкенд дедуплицирует по SHA256). Если передачи идут —
   не пере-сверяем, чтобы не задублировать ещё не дозагруженные ассеты.
   В той же ветке сбрасываются `inFlightCount = 0` и `assetByJobID` — события
   по мёртвым job'ам уже не придут, счётчик не должен застрять.
- `confirmedInCloudIDs: Set<String>` — ассеты, подтверждённые на сервере (classify
  увидел `exists==true`). Только они безусловно пропускаются при пере-сверке.

### Auto-refresh сетки «Альбомы» по завершении автозагрузки

`Features/Media/MediaGridScreen.swift` (сегменты Фото/Видео вкладки «Альбомы») —
`.onChange(of: env.uploadProgress.isActive)`: когда баннер прогресса гаснет
(`true → false`) и `currentSource == .backup`, вызывает `vm.reload()`. Так
свежезагруженные автобэкапом медиа появляются в сетке, пока вкладка открыта, без
ручного pull-to-refresh. Срабатывает после grace-периода баннера (1.5 с), т.е.
после фактического attach последних файлов.

### Жизненный цикл

**Main app старт** (`AppEnvironment.init`):
1. Создать `BackgroundUploadCoordinator.shared` — сразу при обращении создаёт
   `URLSession` с background-config'ом. Если в системе остались задачи
   предыдущей сессии — делегатные события придут в эту инстанцию.
2. Установить `tokenProvider`, completion-observer (attachFile: `.backup` и
   `.share` без папки → `route_by_media_kind`; иначе → в `directoryID`),
   `onPersistentFailure` (schedule BGTask).
3. `attachAndResubmitOrphans()`: для UploadJob в `running` без живой URLSession
   task — сбросить в `pending` и submit заново. Это случается после kill main app.

**Share Extension** (`ShareViewController.processAndFinish`):
1. Проверить `sessionStore.hasValidRefreshToken()` — если нет, показать сообщение
   и закрыться.
2. Собрать вложения через NSItemProvider (image/movie/pdf/fileURL/data).
3. Для каждого:
   - Скопировать оригинал в `<App Group>/UploadStaging/<uuid>-<name>`.
   - `FileTransferService.getUploadURL(.cloudFile)` через gRPC.
   - `MultipartBodyBuilder.writeMultipartFile(...)` в App Group.
   - `UploadQueueStore.shared.create(source: .share, ...)`.
   - `BackgroundUploadCoordinator.shared.submit(jobID:)` — стартует
     `uploadTask`, координатор привязывает `taskIdentifier` и зовёт
     `UploadLiveActivityController.notifyChanged()`.
4. `extensionContext.completeRequest`. Демон iOS продолжает.
5. При завершении задачи — `application(_:handleEventsForBackgroundURLSession:)`
   в main app будит её, координатор обрабатывает все pending события и зовёт
   completion handler.

**BackupManager** (`Features/Gallery/Backup/BackupManager.swift`, `uploadLoop`):
- Лимит 5 одновременных backup-job в очереди (`UploadQueueStore.activeJobs()`).
- `DeviceAssetResource.writeOriginal(asset:to:)` — поток в файл через
  `PHAssetResourceManager.requestData` (без RAM, важно для видео).
- `cloud.enqueueBackgroundUpload(sourceFile:fileName:toDirectory: nil, source: .backup)`
  — **без папки**. Привязку делает координатор через completion-observer
  AppEnvironment с `route_by_media_kind`: сервер сам кладёт в системные
  «Фото»/«Видео»/«Другие документы» по типу медиа. Папка «Недавно загруженные»
  больше не создаётся (метод `ensureRecentUploadsFolder` удалён).
- **Учёт по факту завершения** (2026-06-11): `uploadDone`/`uploadFailed` двигаются
  НЕ при подаче в очередь, а по completion/failure-событиям координатора
  (`BackupManager` подписан в init; карта `assetByJobID: [jobID → PHAsset]`,
  чужие job'ы — manual/share/прошлые запуски — отфильтровываются).
  `remainingCount = pendingUpload.count + inFlightCount` (поданные, но не
  завершённые). Раньше «✓ всё загружено» показывалось, когда передачи ещё шли.
- В `reclaimable` (предложить удалить оригинал) ассет попадает сразу по
  completed-событию (сервер подтвердил 2xx) или когда скан подтвердит его
  через `classify` — но не при простой подаче в очередь.
- По completed-событию также: `CloudDeviceLinkStore.link(fileID:localIdentifier:)`
  и notification `.backupAssetUploaded` → `CloudPresenceTracker` каждого экрана
  сразу рисует бейдж «в облаке» (иначе закэшированное «не в облаке» висело до
  pull-to-refresh — это был баг «не все помечаются как загруженные»).
- `onOpen()` (открытие модалки BackupSheet): если первый скан уже был —
  `refreshScanForNewAssets()`, чтобы состояние (числа, кнопка «Освободить
  место») не оставалось снимком прошлого открытия.
- `setAutoUpload(true)`: `processedAssetIDs = confirmedInCloudIDs ∪ {pendingUpload}`
  (НЕ `removeAll()` — иначе скан надублировал бы уже стоящие в очереди ассеты).
  При выключении — `inFlightCount = 0`, `assetByJobID.removeAll()` до отмены
  jobs, чтобы их failure-события не попали в счётчик ошибок.

**Manual** (`CloudBrowserViewModel.upload`):
- Сразу `cloud.enqueueBackgroundUpload(data:fileName:directoryID:)`. UI не
  ждёт завершения — снэкбар по `upload_failed` только если подача в очередь
  упала.

### Capabilities и build settings

**Entitlements:**

`BarkCloud.entitlements` и `ShareExtension.entitlements`:
```xml
<key>com.apple.security.application-groups</key><array><string>group.com.barkfluff.BarkCloud</string></array>
<key>keychain-access-groups</key><array><string>$(AppIdentifierPrefix)com.barkfluff.BarkCloud</string></array>
```

`BarkCloudWidgets.entitlements`: только `application-groups`.

**Main app build settings:**
- `INFOPLIST_KEY_NSSupportsLiveActivities = YES`
- `INFOPLIST_KEY_NSSupportsLiveActivitiesFrequentUpdates = YES`
- `INFOPLIST_KEY_UIBackgroundModes = processing`
- `INFOPLIST_KEY_BGTaskSchedulerPermittedIdentifiers = com.barkfluff.BarkCloud.upload.retry`

**ShareExtension Info.plist:** `NSSupportsLiveActivities = YES`.

**SessionStore.swift** — `kSecAttrAccessGroup = "$(AppIdentifierPrefix)com.barkfluff.BarkCloud"`
к Keychain-запросам, но `#if !targetEnvironment(simulator)` — на симуляторе
access group не работает.

### BGTaskScheduler retry

`App/AppDelegate.swift`:
- `application(_:didFinishLaunchingWithOptions:)` — `BGTaskScheduler.register`
  с identifier `com.barkfluff.BarkCloud.upload.retry`.
- Обработчик: вычитать `failedJobs(maxRetries: 3)`, для каждого
  `incrementRetries` + `resetForRetry` + `submit` через координатор.
- `scheduleRetryBGTaskIfNeeded()` — глобальная функция-хелпер. Создаёт
  `BGProcessingTaskRequest` с `requiresNetworkConnectivity = true`,
  `earliestBeginDate = now + 5min`. Идемпотентен по identifier'у.

### Скрипты pbxproj

Проект использует filesystem-synchronized groups в Xcode 16. Для добавления
target'ов и shared file references написаны:

- `setup_widgets_target.rb` — создаёт `BarkCloudWidgets` target (app extension),
  `Shared/` PBXGroup с `UploadActivityAttributes.swift` (membership: main +
  widget), Embed App Extensions phase main target → widget.appex, target
  dependency, `INFOPLIST_KEY_NSSupportsLiveActivities` в main.
- `setup_share_extension_sources.rb` — добавляет explicit `PBXFileReference`
  на 25 Swift-файлов из `BarkCloud/Networking/`, `Session/`, `Data/Cache/`,
  `Generated/Proto/` к Share Extension target Sources, плюс линкует
  SwiftPM products GRPCCore / GRPCNIOTransportHTTP2 / GRPCProtobuf / SwiftProtobuf.
- `setup_bgtasks_info.rb` — `INFOPLIST_KEY_UIBackgroundModes` и
  `INFOPLIST_KEY_BGTaskSchedulerPermittedIdentifiers` для main target.

Скрипты идемпотентны (если объект уже в pbxproj — не дублируется). Их можно
выполнить заново после `git pull`, если кто-то их случайно откатил.

## Известные ограничения

1. **Live Activity не обновляется при kill main app**. Background URLSession
   продолжает грузить, но `Activity.update(...)` работает только из живого
   процесса (или через push token, которого мы не используем). При следующем
   запуске main app Live Activity подхватится и догонит.

2. **Лимит памяти Share Extension** — ~120 MB. Подключение всего gRPC stack
   (GRPCCore + NIO + SwiftProtobuf) близко к границе. При большом количестве
   параллельных загрузок может потребоваться оптимизация (REST endpoint для
   GetUploadURL, чтобы убрать gRPC из extension).

3. **Симулятор**: Keychain access group игнорируется, поэтому Share Extension
   на симуляторе видит только свои токены. Тест на устройстве.

4. **Self-signed TLS** в background URLSession: проверяется в
   `BackgroundUploadCoordinator.didReceive challenge:` — host совпадает с
   `GrpcEndpoint.host` (`cloud.barkfluff.com`). Production-сценарий с
   доверенным сертификатом эту проверку обходит.

## Файлы

| Путь | Назначение |
|---|---|
| `Networking/BackgroundUploadCoordinator.swift` | Координатор background URLSession |
| `Networking/UploadConstants.swift` | App Group ID, session ID, BGTask ID, multipart boundary, staging dir |
| `Networking/UploadLiveActivityController.swift` | Управление Live Activity |
| `Networking/MultipartBodyBuilder.swift` | Стрим multipart body в файл |
| `Networking/FileTransferService.swift` | + `validAccessToken()` для координатора |
| `Data/Cache/UploadJob.swift` | SwiftData @Model + UploadJobSnapshot |
| `Data/Cache/UploadQueueStore.swift` | actor поверх SwiftData |
| `Data/Cloud/CloudRepository.swift` | + `enqueueBackgroundUpload(sourceFile:/data:)` |
| `App/AppDelegate.swift` | UIApplicationDelegate + BGTask handler + `scheduleRetryBGTaskIfNeeded` |
| `App/BarkCloudApp.swift` | + `@UIApplicationDelegateAdaptor(AppDelegate.self)` |
| `App/AppEnvironment.swift` | + `backgroundUploads`, хуки на координатор |
| `Session/SessionStore.swift` | + `kSecAttrAccessGroup` |
| `Features/Gallery/DeviceAssetResource.swift` | + `writeOriginal(asset:to:)` (поток) |
| `Features/Gallery/Backup/BackupManager.swift` | `uploadLoop` через `enqueueAssetForBackup` |
| `Features/Files/UI/CloudBrowserViewModel.swift` | `upload()` через `enqueueBackgroundUpload` |
| `Features/ShareInbox/ShareInboxUploader.swift` | Legacy миграция: file → UploadJob |
| `Shared/UploadActivityAttributes.swift` | `ActivityAttributes` (membership: main+widget+share) |
| `BarkCloudWidgets/BarkCloudWidgetsBundle.swift` | WidgetBundle entry |
| `BarkCloudWidgets/UploadLiveActivity.swift` | SwiftUI Live Activity views |
| `ShareExtension/ShareViewController.swift` | gRPC + multipart + submit + Live Activity |
