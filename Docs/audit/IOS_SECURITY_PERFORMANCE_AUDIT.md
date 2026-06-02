# Аудит безопасности, производительности и качества кода iOS-клиента BarkCloud

> Пошаговая методология проверки нативного iOS-клиента BarkCloud.
> Версия документа: 1.0 · Дата: 2026-06-02 · Охват: iOS-приложение + Share Extension + Widgets.
> Дополняет backend-методологию `Docs/audit/SECURITY_PERFORMANCE_AUDIT.md` (мобильные клиенты в неё не входят).

## 0. Введение

### 0.1 Цель
Систематически и **повторяемо** проверить нативный iOS-клиент BarkCloud в трёх плоскостях —
**безопасность**, **производительность**, **качество кода**. Документ — методология «по шагам»:
что проверять, где смотреть (якоря на файлы) и какой предварительный приоритет у каждого шага.
Фиксация находок — в едином формате (раздел 5). Сами находки в этом документе не приводятся:
он описывает процесс, а не результат.

### 0.2 Охват
- **В охвате:**
  - `Ios/BarkCloud/BarkCloud/` — основное приложение: `App/`, `Session/`, `Networking/`, `Data/`,
    `Features/`, `Theme/`, `Resources/`, `Generated/Proto/` (как граница доверия, не сам код стабов).
  - `Ios/BarkCloud/ShareExtension/` — расширение «Поделиться» (отдельный процесс).
  - `Ios/BarkCloud/BarkCloudWidgets/` — Live Activity / виджеты.
  - `Ios/BarkCloud/Shared/` — общие для таргетов типы (`UploadActivityAttributes` и т.п.).
  - Entitlements: `BarkCloud.entitlements`, `ShareExtension.entitlements`, `BarkCloudWidgets.entitlements`.
  - Build-settings и capabilities в `BarkCloud.xcodeproj/project.pbxproj` (ATS, App Group, Keychain
    access-group, background modes, BGTaskScheduler ids, `SWIFT_DEFAULT_ACTOR_ISOLATION`).
- **Вне охвата:**
  - Backend и web-клиент — см. `Docs/audit/SECURITY_PERFORMANCE_AUDIT.md`.
  - gRPC-контракт (`Shared/BarkCloud.Proto/*.proto`) и сгенерённые стабы `Generated/Proto/*` —
    проверяются только как доверительная граница (что приходит/уходит), не как исходник.
  - Android-клиент.

### 0.3 Модель доверия (iOS-специфика)
- **Устройство может быть скомпрометировано:** jailbreak, доступ к контейнеру приложения,
  iTunes/iCloud-бэкап, отладчик. Поэтому всё, что лежит вне Keychain (UserDefaults, SwiftData,
  Documents, App Group staging), считается читаемым злоумышленником с доступом к устройству/бэкапу.
- **Общая поверхность процессов:** main app ↔ Share Extension ↔ Widgets делят **App Group**
  (`group.com.barkfluff.BarkCloud`) и **Keychain access-group**. Компрометация/подмена любого
  таргета даёт доступ к общим хранилищам.
- **Сеть:** сервер self-hosted, TLS терминируется на nginx с **self-signed**-сертификатом; клиент
  настроен доверять ему. Канал между приложением и сервером — основная цель MITM.
- **Сервер — не доверенный ввод для клиента:** ответы gRPC/HTTP и presigned-URL приходят извне;
  клиент не должен падать/течь на «битых», огромных или враждебных ответах.

### 0.4 Легенда severity
**Critical** — утечка токенов/учётных данных или чужих данных, полный обход TLS (MITM без условий),
выполнение кода.
**High** — обход локальной защиты (App Lock/Vault), OOM/зависание UI на штатных данных,
доступность секретов другому процессу/в бэкапе.
**Medium** — ослабление защиты, метаданные/PII в небезопасном хранилище, заметная деградация под
нагрузкой, утечка памяти.
**Low** — hardening, устаревшие практики, мёртвый код, нелокализованные строки, стиль.

---

## 1. Подготовка окружения и инструментов

1. **Сборка и тесты** из `Ios/BarkCloud`:
   ```bash
   xcrun simctl list devices available          # выбрать актуальный симулятор (iOS 26.x)
   xcodebuild -project BarkCloud.xcodeproj -scheme BarkCloud \
     -destination 'platform=iOS Simulator,name=iPhone 17' build
   xcodebuild test -project BarkCloud.xcodeproj -scheme BarkCloud \
     -destination 'platform=iOS Simulator,name=iPhone 17'
   ```
2. **Статический анализ:** `xcodebuild analyze …` (или Product → Analyze); сборка со **strict
   concurrency** (`SWIFT_STRICT_CONCURRENCY=complete`) для отлова data races; ревью warning'ов.
3. **Профилирование (Instruments):** Time Profiler (main-thread), Allocations/Leaks (память и циклы),
   SwiftUI (ре-рендеры/«view body»), Network. Запускать на **устройстве** для реалистичных цифр.
4. **Сеть:** **Network Link Conditioner** (медленная/обрывающаяся сеть); **mitmproxy/Charles** —
   проверить, перехватывается ли трафик (обход TLS) и не утекают ли presigned/temp-URL.
5. **Логи:** Console.app на устройстве/симуляторе — искать токены/пароли/URL в выводе приложения и
   Share Extension.
6. **Файловая система:** контейнер приложения и App Group из симулятора (`xcrun simctl get_app_container`)
   — посмотреть, что реально лежит в UserDefaults/SwiftData/staging.
7. **Тестовые данные:** 2 аккаунта (кросс-аккаунтные кэши/Vault), большая медиатека (10k+ фото,
   тяжёлые видео ≥1 ГБ), «битые»/огромные изображения, второй процесс в App Group.

> **Примечание:** покрытие тестами в репозитории минимально (`BarkCloudTests` — только обслуживающая
> логика дискового кеша). Большая часть проверок выполняется **статически** (ревью по якорям раздела 3)
> и **динамически** (Instruments, mitmproxy, сценарии раздела 4).

---

## 2. Сквозные этапы (И1–И5)

Применяются к каждой области раздела 3.

- **И1. Инвентаризация и поверхность атаки.** Таргеты и их entitlements; App Group и Keychain
  access-group; фоновые режимы и BGTask-id; точки входа (URL-схемы, Share Extension, обработка
  фоновой URLSession). Карта потоков чувствительных данных (токены, presigned-URL, PIN, медиа).
- **И2. Хранилища на устройстве.** Для каждого факта данных — где лежит (Keychain / App Group
  UserDefaults / `.standard` UserDefaults / SwiftData / Documents / staging), атрибут доступности
  (`kSecAttrAccessible*`), шифрование at-rest, и **чистится ли при полном сбросе**
  (`AppEnvironment.resetLocalState()`).
- **И3. Сеть и доверие.** TLS-делегаты и верификация сертификата; присоединение токена; нет ли
  секретов/URL в логах; обработка враждебных/больших ответов сервера.
- **И4. Ресурсы.** Пиковая память на больших списках/медиа; блокировки main-thread/MainActor;
  лимиты и eviction дискового кеша; стриминг vs загрузка целиком в RAM.
- **И5. Конкурентность и корректность.** Изоляция (`actor`/`Sendable`/`@unchecked Sendable`);
  жизненный цикл `Task` (хранение в `@Observable`-state, чтобы перерисовка не отменяла сетевую
  операцию); обработка ошибок без «глотания».

---

## 3. Проверки по областям

Формат шага: **[предв. severity]** «проверить, что …». **Anchors** — где смотреть (пути от
`Ios/BarkCloud/BarkCloud/`, если не указано иное). Severity — предварительная, подтверждается при
выполнении.

### 3.A Безопасность

**A1. TLS и доверие сертификатам**
1. **[Critical]** Как реализовано доверие self-signed: ограничен ли обход **конкретным хостом**
   (`challenge.protectionSpace.host`) или применяется широко; **не отключается ли верификация
   целиком** для gRPC при `allowSelfSigned`. Поведение по умолчанию (`ServerConfig.production`).
2. **[High]** Нет ли certificate pinning и нужен ли он для self-hosted-боя; что происходит при
   `useTLS == false` (plaintext gRPC).
3. **[Medium]** Согласованность TLS-политики между gRPC, HTTP-аплоадом/скачиванием и фоновой
   URLSession.
- *Anchors:* `Networking/InsecureURLSession.swift`, `Networking/BackgroundUploadCoordinator.swift`
  (TLS-делегат), `Networking/GrpcManager.swift` (`client(host:port:)`, `ServerConfig`),
  `Features/ServerSetup/ServerSetupScreen.swift`.

**A2. Токены и секреты (Keychain)**
1. **[High]** Атрибут доступности токенов (`kSecAttrAccessible*`): не шире ли необходимого
   (доступ при заблокированном экране/после первого анлока); привязка к устройству
   (`…ThisDeviceOnly`).
2. **[Medium]** Keychain access-group задаётся явно или по неявному авто-назначению; кто из таргетов
   читает токены.
3. **[Medium]** Какие RPC не аутентифицируются и почему (исключение стейл-токена для `Auth`/`CreateToken`);
   нет ли присоединения токена к публичным методам.
4. **[Medium]** Полная очистка токенов при выходе/удалении/wipe.
- *Anchors:* `Session/SessionStore.swift`, `Networking/AuthInterceptor.swift`,
  `Networking/GrpcManager.swift` (`unauthenticatedMethods`, `validAccessToken`),
  `Data/Auth/AuthRepository.swift`, `App/AppEnvironment.swift` (`resetLocalState`).

**A3. App Lock (криптография PIN)**
1. **[High]** Параметры PBKDF2 (алгоритм, число итераций, длина ключа), генерация соли
   (CSPRNG), constant-time сравнение хеша.
2. **[High]** Где хранятся хеш/соль (Keychain + атрибут) и **счётчик неверных попыток** (если в
   UserDefaults — подделываем ли сброс лимита).
3. **[Medium]** Логика wipe по N неверным попыткам: полнота сброса, окно между неверным PIN и wipe.
4. **[Medium]** Биометрия: политика (`deviceOwnerAuthentication`), повторная блокировка при возврате
   из фона (grace-window).
- *Anchors:* `Data/Cache/AppLockSettings.swift`, `Features/AppLock/AppLockManager.swift`,
  `Features/Vault/BiometricGate.swift`, `Features/AppLock/SetPinSheet.swift`.

**A4. Локальный «сейф» (Vault)**
1. **[High]** Где хранится список защищённых файлов и **шифруется ли** он; это реальная защита или
   только UI-фильтр поверх обычной галереи (сервер о «защите» не знает).
2. **[Medium]** Попадает ли список (факт «файл в сейфе», `file_id`, preview-URL) в бэкап; чистится
   ли при сбросе; используется ли App Group или `.standard` UserDefaults.
- *Anchors:* `Features/Vault/VaultStore.swift`, `VaultScreen.swift`, `VaultViewModel.swift`.

**A5. App Group и shared-хранилища**
1. **[High]** Что чувствительного лежит в общем контейнере: `ServerConfig` (хосты/порты/флаг
   `allowSelfSigned`), очередь загрузок (`UploadQueue.sqlite`), staging-файлы — и доступно ли это
   другому процессу группы.
2. **[Medium]** Шифрование SwiftData-БД очереди; метаданные (имена/пути/URL) в открытом виде.
3. **[Medium]** Очистка staging и App Group UserDefaults при сбросе.
- *Anchors:* `Networking/UploadConstants.swift`, `Data/Cache/UploadQueueStore.swift`,
  `Data/Cache/UploadJob.swift`, `App/ServerConfigStore.swift`, `Networking/GrpcManager.swift`
  (`ServerConfig`), `*.entitlements`.

**A6. Share-ссылки и буфер обмена**
1. **[Medium]** Что копируется в `UIPasteboard`: presigned/temp-download-URL с токеном? Срок жизни
   ссылки; очищается ли буфер; не попадает ли в Universal Clipboard/историю.
2. **[Low]** Сборка публичной share-ссылки (`/s/{token}`), экранирование, отсутствие токена в логах.
- *Anchors:* `Networking/GrpcManager.swift` (`publicShareURL`), `Networking/FileTransferService.swift`
  (`tempDownloadURLs`), `Features/Gallery/GalleryViewModel.swift`, `Features/Media/*` (copyLink),
  `Features/Shared/ShakeContextMenu.swift`.

**A7. Логирование и приватность**
1. **[Medium]** Нет ли `print`/`NSLog`/`os_log` токенов, паролей, PIN, presigned/temp-URL,
   refresh-токена; что пишется при ошибках сети/refresh.
2. **[Low]** Риск утечки при будущем подключении стороннего логгера/краш-репортера (сериализация
   protobuf-запросов с токенами).
- *Anchors:* поиск по `print(` / `NSLog` / `os_log` / `debugPrint` во всех таргетах; особое внимание
  `Networking/*`, `Session/SessionStore.swift`, `Data/Auth/*`.

**A8. Share Extension**
1. **[High]** Чтение токена из shared Keychain: тот же access-group, корректность запроса; нет ли
   собственного, рассинхронизированного состояния сессии.
2. **[Medium]** Тот же TLS-обход, что в main app; staging-файлы остаются ли после краша расширения.
- *Anchors:* `ShareExtension/ShareViewController.swift`, `ShareExtension.entitlements`.

### 3.B Производительность

**B1. Загрузка и кеш изображений**
1. **[Medium]** Декодирование изображений вне main-thread; downsampling под целевой размер ячейки;
   нет ли декодирования полноразмерных оригиналов в сетках.
2. **[Medium]** Лимиты `NSCache` (count/cost) и реакция на memory warning; дедупликация in-flight
   запросов одной картинки.
- *Anchors:* `Features/Shared/RemoteImage.swift`, `Features/Shared/MediaThumb.swift`,
  `Features/Files/UI/ThumbnailLoader.swift`.

**B2. Дисковый кеш (SwiftData)**
1. **[High]** `enforceSizeLimit`/eviction: не загружаются ли **все** записи в память для LRU;
   есть ли `fetchLimit`/батчинг.
2. **[Medium]** Переиспользование `ModelContext` vs создание на каждый вызов; чтение файла —
   стримингом или `Data(contentsOf:)` целиком в RAM.
- *Anchors:* `Data/Cache/FileCacheService.swift`, `Data/Cache/CachedFileEntry.swift`,
  `Data/Cache/FileCacheSettings.swift`.

**B3. SHA256-хеширование**
1. **[High]** Когда запускается хеширование (на появлении ячейки/скролле) и **ограничен ли
   параллелизм**; не плодятся ли десятки одновременных задач при быстром скролле.
2. **[Medium]** Стриминговое чтение (а не файл целиком) и переиспользование кеша хешей; совпадение
   с серверным хешем.
- *Anchors:* `Features/Gallery/DeviceAssetResource.swift`, `Features/Gallery/CloudPresenceTracker.swift`,
  `Data/Cache/AssetHashStore.swift`.

**B4. Скан и бэкап медиатеки**
1. **[High]** Загрузка всех `PHAsset` в массив до фильтрации (пиковая память на 10k+); память при
   обработке видео; конкурентность скана.
2. **[Medium]** Батчинг `CheckFileHashes`; адаптивность лимита одновременных аплоадов; повторные
   PhotoKit-запросы (например, размер файла).
- *Anchors:* `Features/Gallery/Backup/BackupManager.swift`.

**B5. Пагинация и память списков**
1. **[High]** Курсорная пагинация: растут ли массивы без предела при скролле к концу; удержание всей
   медиатеки/директории в памяти.
2. **[Medium]** Стабильная identity в `ForEach`; отсутствие лишних повторных fetch'ов.
- *Anchors:* `Features/Media/MediaGridViewModel.swift`, `Features/Trash/TrashViewModel.swift`,
  `Features/Media/Albums/AlbumsViewModel.swift`, `Features/Files/UI/CloudBrowserViewModel.swift`,
  `Features/Gallery/GalleryViewModel.swift`.

**B6. Main-thread / MainActor**
1. **[High]** Тяжёлая работа на MainActor (`SWIFT_DEFAULT_ACTOR_ISOLATION=MainActor`): хеширование,
   файловый I/O, decode, перестройка больших массивов, ожидание gRPC внутри MainActor-методов.
2. **[Medium]** Синхронные операции внутри акторов, блокирующие изоляцию.
- *Anchors:* `Features/Gallery/Backup/BackupManager.swift`, `Features/Gallery/GalleryViewModel.swift`
  (`handleLibraryChange`), `Data/Cache/FileCacheService.swift`.

**B7. Аплоады**
1. **[High]** Строится ли тело multipart **в памяти** (синхронный путь) vs стриминг из файла
   (фоновый путь); удержание нескольких `Data` выбранных файлов в RAM до постановки в очередь.
2. **[Low]** Размер чанка и двойная запись (write + move) в staging.
- *Anchors:* `Networking/FileTransferService.swift`, `Networking/MultipartBodyBuilder.swift`,
  `Data/Cloud/CloudRepository.swift` (`uploadFile`), `Features/Files/UI/CloudBrowserViewModel.swift`.

**B8. SwiftUI ре-рендеры**
1. **[Medium]** `@Observable`-объекты с множеством часто меняющихся полей (счётчики прогресса)
   → перерисовка крупных экранов на каждый инкремент.
2. **[Medium]** Словарные lookup'ы (`presence[id]`) в каждой ячейке сетки; полная перерисовка сетки
   при переключении режима выбора; дорогие вычисления в `body`.
- *Anchors:* `Features/Media/MediaGridScreen.swift`, `Features/Gallery/GalleryScreen.swift`,
  `Features/Gallery/Backup/BackupSheet.swift`, `Features/Gallery/Backup/BackupManager.swift`.

### 3.C Качество кода

**C1. Конкурентность**
1. **[High]** `@unchecked Sendable` с ручным `NSLock` вместо `actor` (риск пропущенного лока/гонки);
   полнота защиты всех путей доступа к разделяемому состоянию.
2. **[Medium]** Жизненный цикл `Task`: хранятся ли долгие задачи в `@Observable`-state, чтобы
   перерисовка/закрытие view не рвали сетевую операцию; корректная отмена.
3. **[Low]** Конформанс `Sendable` value-типов, изоляция ViewModel.
- *Anchors:* `Networking/BackgroundUploadCoordinator.swift`, `Networking/GrpcManager.swift`,
  акторы `Data/Cache/*`, ViewModels с `Task`-полями (`UploadProgressObserver`, `PendingDelete`,
  `BackupManager`).

**C2. Обработка ошибок**
1. **[Medium]** `force unwrap (!)`, `try!`, `as!`, `fatalError` — в штатных путях (не только в
   init/recovery); заменимы ли на guard/optional.
2. **[Medium]** Массовые `try?`, глотающие ошибки SwiftData/файлового I/O без логирования
   (риск незаметной потери персистентности).
- *Anchors:* `App/AppDelegate.swift` (`as!` BGTask), `App/AppEnvironment.swift` (`try!` ModelContainer),
  `Data/Cache/{UploadQueueStore,AssetHashStore,FileCacheService}.swift`,
  поиск по `try!` / `as!` / `fatalError(` / `!` в горячих путях.

**C3. Retain cycles и память**
1. **[High]** Захват `self` и сервисов/репозиториев в замыканиях `Task`/делегатах/наблюдателях:
   `[weak self]` против сильного захвата зависимостей; нет ли цикла «VM → замыкание → сервис → VM».
2. **[Medium]** Наблюдатели PhotoKit/NotificationCenter/таймеры — снимаются ли; делегаты — weak.
- *Anchors:* `Features/Files/UI/CloudBrowserViewModel.swift`, `LocalBrowserViewModel.swift`,
  `Features/Gallery/GalleryViewModel.swift` (library observer), `Networking/BackgroundUploadCoordinator.swift`.

**C4. Дублирование**
1. **[Low]** Повторяющийся `reload(showSpinner:)` по нескольким browser-VM; нормализация URL;
   бойлерплейт создания gRPC-стабов — выносимо ли в общий слой.
- *Anchors:* browser-ViewModels (`Trash`/`Albums`/`Cloud`/`Media`), `Networking/GrpcManager.swift`
  (стабы), `Networking/GrpcManager.swift` (`normalizedFileDownloadURL`).

**C5. Архитектура и тестируемость**
1. **[Medium]** Живые синглтоны (`*.shared`) и статическое состояние (`ServerConfig.current` поверх
   UserDefaults) затрудняют DI и юнит-тесты; чистота слоя репозиториев; не «протекает» ли сеть во вью.
2. **[Low]** Размер сервис-локатора `AppEnvironment` (число зависимостей), целесообразность разбиения.
- *Anchors:* `App/AppEnvironment.swift`, `Data/**`, `Networking/GrpcManager.swift` (`ServerConfig.current`),
  `BarkCloudTests/`.

**C6. Мёртвый код и миграции**
1. **[Low]** Одноразовые миграции без срока удаления (`ShareInbox*`), заглушки (`ComingSoonScreen`),
   legacy-ветки (например, путь без кеша в `RemoteImage`); пометить для удаления/версии.
- *Anchors:* `Features/ShareInbox/*`, `Features/Shared/ComingSoonScreen.swift`,
  `Features/Shared/RemoteImage.swift`.

**C7. Локализация и magic values**
1. **[Low]** Захардкоженные русские строки в коде вместо строкового каталога (имена авто-папок,
   тексты диалогов).
2. **[Low]** «Магические» числа (таймауты, batch-размеры, окна undo, debounce) — именованы ли,
   согласованы ли между собой.
- *Anchors:* `Data/Cloud/CloudRepository.swift` (`recentUploadsFolderName`),
  `Features/Gallery/DeviceAssetPickerScreen.swift`, тайминги в `UploadProgressObserver.swift`,
  `Features/Shared/PendingDelete.swift`, `Features/Gallery/Backup/BackupManager.swift`,
  `Resources/Localizable.xcstrings`.

---

## 4. Нагрузочные и стресс-сценарии

- **Большая медиатека (10k+ фото):** время холодного скана (`BackupManager`), пиковая память
  (Instruments → Allocations), плавность скролла галереи и сеток, число одновременных задач
  хеширования при быстром скролле.
- **Большие файлы (видео ≥1 ГБ):** фоновый аплоад, пиковая RAM при построении тела, поведение
  дискового кеша и staging, корректность Live Activity.
- **Медленная/обрывающаяся сеть (Network Link Conditioner):** проактивный refresh токена, retry
  фоновых задач (BGTask), отсутствие зависаний UI, корректные сообщения об ошибках.
- **Жизненный цикл/фон:** App Lock grace-window при возврате из фона, докачка очереди после
  перезапуска, переживание kill-процесса фоновой загрузкой.
- **Враждебные ответы сервера:** пустые/битые/огромные ответы, недостижимые presigned-URL —
  клиент не падает и не течёт.

---

## 5. Шаблон отчёта о находке

Заполняется при выполнении аудита (в этом документе — только форма).

| Поле | Значение |
|---|---|
| **ID** | `IOS-SEC-01` / `IOS-PERF-01` / `IOS-CODE-01` |
| **Область** | Security / Performance / Code |
| **Severity** | Critical / High / Medium / Low |
| **Файл:строка** | `Networking/GrpcManager.swift:NNN` |
| **Описание** | Что не так и почему это проблема в модели доверия (раздел 0.3) |
| **Воспроизведение** | Шаги / условия / инструмент (Instruments, mitmproxy, …) |
| **Рекомендация** | Что изменить |
| **Статус** | Open / In progress / Fixed / Won't fix |

Сводка в конце: матрица «область × severity» и приоритеты ремедиации (как в
`Docs/audit/SECURITY_AUDIT_FINDINGS.md`).

---

## 6. Приложение: карта iOS-клиента

Навигационная подсказка для исполнителя (без оценок).

```
Ios/BarkCloud/
├── BarkCloud/                     основное приложение
│   ├── App/                       @main, AppEnvironment (сервис-локатор), RootView (гейты), AppDelegate (BGTask/фон URLSession)
│   ├── Session/                   SessionStore (Keychain-токены)
│   ├── Networking/                GrpcManager (actor) + интерсепторы, TLS-делегаты, FileTransferService, BackgroundUploadCoordinator, Multipart
│   ├── Data/                      Auth / Users / Cloud (репозитории), Cache (SwiftData: FileCache, UploadQueue, AssetHash, AppLock, настройки)
│   ├── Features/                  ServerSetup, Login, Main, Gallery(+Backup), Files, Media(+Albums), Trash, Settings, AppLock, Vault, Shared, ShareInbox
│   ├── Theme/ Resources/ Generated/Proto/ Assets.xcassets/
├── ShareExtension/                расширение «Поделиться» (свой процесс, shared Keychain/App Group)
├── BarkCloudWidgets/              Live Activity / виджеты
└── Shared/                        общие типы таргетов
```

**Границы доверия для приоритета проверок:** App Group (`group.com.barkfluff.BarkCloud`) и Keychain
access-group — общие для трёх таргетов; всё в App Group UserDefaults / SwiftData / staging читаемо
из бэкапа и из любого процесса группы. TLS-обход и хранение токенов — Critical/High; локальные
кэши и метаданные — Medium; стиль/мёртвый код/локализация — Low.
