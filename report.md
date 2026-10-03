# Backend BarkCloud — что осталось исправить

Дата повторной проверки: **2026-10-03**. Ветка: `master`. Проверенный срез: **`575318ac230812ed974b0d3b8ab81aadca1b5580`**.

Пересмотрены все 27 исходных находок и 66 коммитов после `8c788b66512f194c1a90354ff461045e2dc77e05`, включая последние 12 после `fc088d6`. Ниже только оставшаяся работа: **12 пунктов — 2 P1, 9 P2, 1 P3**. Закрытые находки исключены. Семь пунктов исправлены частично, пять остаются открытыми.

Штатные тесты: **1341/1341 прошли**, 0 ошибок, 0 пропусков, 15 проектов backend/shared. Прогон выполнен на неизменяемой архивной копии проверенного коммита с изолированными PostgreSQL 18 и RabbitMQ; в двух интеграционных проектах 51 тест, все прошли. В логах этого прогона нет `NU1903` и `CS0436`.

После штатного прогона шесть дополнительных диагностических тестов воспроизвели остатки F02, F13, F15, F19, F20 и F23. Эти проверки ожидаемого корректного поведения **падают на текущем коде** и не включены в 1341 штатный тест. Временные тесты добавлялись только в архивную копию вне рабочего репозитория; исходные файлы копии затем восстановлены. Production-код аудитом не менялся.

## Оставшиеся находки

| ID | Приоритет | Область | Что требуется |
|---|---|---|---|
| F02 | P1 | Identity / отзыв сессий | Не пропускать JWT, выпущенный по старому refresh после reset |
| F13 | P1 | Files / дерево | Не восстанавливать удалённое состояние записью rename |
| F15 | P2 | Files / удаление блобов | Согласовать AttachFile с purge оригинала |
| F17 | P2 | Web / Torrent | Настроить время legacy upload/import и отмену в Web |
| F18 | P2 | Files / очередь | Освобождать слоты consumer во время длинных повторов |
| F19 | P2 | Identity | Устранить потерю уведомления и безграничное ожидание RegisterDevice |
| F20 | P2 | Files / поиск | Согласовать SQL-совпадение и поля результата |
| F21 | P2 | Torrent / статистика | Не терять дельту при отказе SaveChanges |
| F22 | P2 | Torrent / команды | Согласовать Paused в БД с состоянием движка |
| F23 | P2 | Shared / JWT | Проверять при запуске реальную минимальную длину HS256-ключа |
| F24 | P2 | CI | Покрыть общие зависимости и тесты Torrent |
| Q03 | P3 | Сопровождение | Уменьшать крупные классы и дублирование |

### F02. Старый refresh другого устройства может выдать действующий JWT после reset — P1

**Где:** [CreateTokenCommandHandler.cs:19](Backend/BarkCloud.Identity/Features/CreateToken/CreateTokenCommandHandler.cs#L19), [RefreshTokensStorage.cs:81](Backend/BarkCloud.Identity/Persistence/Services/RefreshTokensStorage.cs#L81), [TokenRevocationCache.cs:37](Backend/BarkCloud.GrpcServer/XAuth/TokenRevocationCache.cs#L37).

**Проблема:** обновление токена читает refresh без согласования со сбросом пароля. Порог `MaxSessionId` при reset записывается только для текущего устройства; остальные устройства отзываются по времени. Запрос другого устройства может прочитать старый refresh до reset, продолжиться после его commit и выпустить JWT с `iat > RevokedAt`. Полностью синхронизированный кэш пропускает такой токен, хотя его `sid` относится к удалённой сессии.

**Подтверждение:** дополнительный тест реальных reset/CreateToken/feed/cache на PostgreSQL: старый `sid=2`; `RevokedAt=22:10:09.670049Z`, `iat=22:10:10Z`; `IsRevoked=false`. Это гонка выпуска, не задержка доставки отзыва.

**Осталось сделать:** применять отзыв старых session ID ко всем отзываемым устройствам либо согласовать выпуск JWT и отзыв так, чтобы чтение старого refresh не приводило к действующей сессии после commit reset. Проверить запрос refresh, остановленный после чтения и продолженный после reset; новая сессия с новым ID должна работать.

### F13. Rename может вернуть файл из корзины в удалённую папку — P1

**Где:** [RenameFileEntryCommandHandler.cs:38](Backend/BarkCloud.Files/Features/Cloud/RenameFileEntry/RenameFileEntryCommandHandler.cs#L38), [CloudHierarchyStorage.cs:330](Backend/BarkCloud.Files/Persistence/CloudHierarchyStorage.cs#L330), [DeleteDirectoryCommandHandler.cs:44](Backend/BarkCloud.Files/Features/Cloud/DeleteDirectory/DeleteDirectoryCommandHandler.cs#L44).

**Проблема:** `RenameFileEntry` не участвует в `LockTree`. `UpdateFileEntry` вызывает `Update(entry)`, помечая изменёнными все поля устаревшей записи. Между чтением и сохранением rename удаление папки может отправить файл в корзину и удалить каталог. Rename затем записывает прежние `IsDeleted=false` и `DirectoryId`, отменяя удаление файла и оставляя живую ссылку на отсутствующий каталог. Та же запись всех полей может затирать конкурентный перенос/изменение состояния файла.

**Подтверждение:** PostgreSQL-тест с остановкой rename перед SaveChanges и настоящим DeleteDirectory: папка отсутствует, rename завершился успешно, запись имеет `IsDeleted=false`.

**Осталось сделать:** согласовать rename с удалением/переносом, брать замок до чтения актуальной записи и сохранять только необходимые поля вместо всего снимка. Проверить rename одновременно с удалением папки, переносом файла и отправкой в корзину: изменение имени не должно менять местоположение и статус удаления.

### F15. AttachFile может создать ссылку на уже удалённый оригинал — P2

**Где:** [AttachFileCommandHandler.cs:68](Backend/BarkCloud.Files/Features/Cloud/AttachFile/AttachFileCommandHandler.cs#L68), [AttachFileCommandHandler.cs:118](Backend/BarkCloud.Files/Features/Cloud/AttachFile/AttachFileCommandHandler.cs#L118), [TrashPurgeService.cs:103](Backend/BarkCloud.Files/Services/TrashPurgeService.cs#L103), [TrashPurgeService.cs:226](Backend/BarkCloud.Files/Services/TrashPurgeService.cs#L226).

**Проблема:** Attach читает готовый блоб и список владельцев, затем отдельно сохраняет `CloudFileEntry`. Purge не использует его замок дерева. После чтения purge может удалить последнюю запись корзины, снять владельца и окончательно удалить оригинал; Attach продолжает по старому снимку и возвращает успех. Внешнего ключа `CloudFileEntry → UploadFile` нет.

**Подтверждение:** настоящие Attach/Purge и PostgreSQL дали одну новую живую запись при отсутствующем `UploadFile`; вызов S3 Delete подтверждён mock. Физический MinIO для этого теста не запускался.

**Осталось сделать:** согласовать проверку владения, создание ссылки и захват оригинала на удаление одной границей конкурентного доступа. Проверить обе очередности Attach/purge; допустимы сохранение существующего оригинала либо отказ Attach, но не успешная ссылка на отсутствующий файл.

### F17. Legacy upload/import обрывается через 100 секунд — P2

**Где:** [Web/Program.cs:89](Backend/BarkCloud.Web/Program.cs#L89), [Torrent/Program.cs:66](Backend/BarkCloud.Torrent/Program.cs#L66), [CloudApiEndpoints.cs:1316](Backend/BarkCloud.Web/Endpoints/CloudApiEndpoints.cs#L1316), [TorrentImportService.cs:66](Backend/BarkCloud.Torrent/Infrastructure/TorrentImportService.cs#L66).

**Проблема:** оба клиента `files-upload` регистрируются без настройки Timeout и наследуют стандартные 100 секунд. Допустимая долгая legacy-загрузка или импорт может оборваться. Web также не передаёт `RequestAborted` в этот `PostAsync`, поэтому отключение браузера не обязательно прекращает upstream-передачу. Torrent отмену уже передаёт.

**Осталось сделать:** настроить допустимое время передачи и передавать отмену браузера в legacy Web upload. Проверить передачу дольше 100 секунд и остановку upstream после отмены. Остаток установлен по коду; длительная передача в аудите не запускалась.

### F18. Длинные retry занимают оба слота обработки медиа — P2

**Где:** [Files/Program.cs:171](Backend/BarkCloud.Files/Program.cs#L171).

**Проблема:** `ConcurrentMessageLimit=2`, in-memory retry — через 10 секунд, 1, 5 и 15 минут. Два сообщения с повторяющейся ошибкой удерживают оба слота на суммарные **21 минуту 10 секунд** ожиданий, дополнительно к времени обработки. Независимые исправные загрузки остаются в очереди.

**Осталось сделать:** длинные повторы выполнять с освобождением consumer-слота, например через отложенную повторную доставку; короткие локальные повторы ограничить. Проверить, что после двух ошибочных сообщений третье исправное обрабатывается без ожидания всей цепочки retry. Конфигурация проверена статически; нагрузочный прогон очереди не выполнялся.

### F19. Уведомление может потеряться, а login — ждать RegisterDevice без срока — P2

**Где:** [NotificationOutbox.cs:46](Backend/BarkCloud.Identity/Services/NotificationOutbox.cs#L46), [NotificationOutbox.cs:50](Backend/BarkCloud.Identity/Services/NotificationOutbox.cs#L50), [SetPasswordCommandHandler.cs:83](Backend/BarkCloud.Identity/Features/SetPassword/SetPasswordCommandHandler.cs#L83), [SessionIssuer.cs:71](Backend/BarkCloud.Identity/Services/SessionIssuer.cs#L71).

**Проблема постановки:** основное изменение пароля/сессии и запись `PendingNotification` сохраняются разными commit. Авария между ними оставляет успешное изменение без уведомления. При ошибке вставки исключение подавляется, запись отсоединяется из tracker; worker не сможет повторить отсутствующую строку.

**Проблема ожидания:** после создания токенов SessionIssuer ждёт `RegisterDeviceAsync` без deadline и без переданного cancellation token. Зависший RPC удерживает результат login даже после отмены запроса.

**Подтверждение:** разрыв commit установлен по коду; штатный `EnqueueAsync_SaveFails_DoesNotThrowAndDetachesEntity` проверяет подавление ошибки вставки. Дополнительный тест настоящего SessionIssuer с незавершённым RPC подтвердил ожидание после отмены; завершение RPC освобождает выдачу результата.

**Осталось сделать:** сохранять основное изменение и намерение отправить уведомление атомарно. Задать срок/отмену регистрации устройства либо вынести её из ожидания результата входа. Проверить отказ вставки/аварию между сохранениями и зависший RegisterDevice.

### F20. SQL-поиск и поля совпадения карточки используют разные алгоритмы — P2

**Где:** [SearchRanking.cs:31](Backend/BarkCloud.Files/Services/SearchRanking.cs#L31), [UnifiedSearchService.cs:299](Backend/BarkCloud.Files/Services/UnifiedSearchService.cs#L299), [UnifiedSearchService.cs:326](Backend/BarkCloud.Files/Services/UnifiedSearchService.cs#L326).

**Проблема:** SQL использует `word_similarity(value, query)`, а `CreateHit/Match` — прежнюю C# Dice-метрику. SQL может включить результат, для которого C# не находит совпадения; карточка получает пустые `MatchField/MatchValue`. Состав результатов также расходится с прежним фильтром.

**Подтверждение:** полный Search на PostgreSQL: папка `report`, запрос `quarterly report finances`; SQL-сходство 1, C# — 0.363636 при пороге 0.45. Папка возвращается с `MatchField=""`. Это ошибка поискового соответствия, а не подтверждённое нарушение доступа.

**Осталось сделать:** согласовать метрику фильтрации, ранжирования и выбора поля совпадения. Зафиксировать ожидаемые результаты многословных/опечаточных запросов и пагинацию; сохранить действующее ограничение объёма чтения SQL.

### F21. Ошибка SaveChanges теряет накопленную дельту трафика — P2

**Где:** [TorrentPersistenceService.cs:93](Backend/BarkCloud.Torrent/Infrastructure/TorrentPersistenceService.cs#L93), [TorrentPersistenceService.cs:114](Backend/BarkCloud.Torrent/Infrastructure/TorrentPersistenceService.cs#L114).

**Проблема:** `LastSessionDownloaded/Uploaded` обновляются в памяти до `SaveChangesAsync`. Если запись в БД упадёт, следующий проход считает дельту от уже сдвинутого baseline и не повторяет потерянную часть статистики.

**Осталось сделать:** двигать baseline только после успешного сохранения соответствующей дельты. Проверить отказ БД в одном проходе и последующее сохранение всей накопленной разницы. Остаток установлен по коду.

### F22. Paused в БД может расходиться с состоянием движка — P2

**Где:** [TorrentEngineService.cs:153](Backend/BarkCloud.Torrent/Infrastructure/TorrentEngineService.cs#L153), [TorrentApiService.cs:286](Backend/BarkCloud.Torrent/Host/TorrentApiService.cs#L286), [TorrentApiService.cs:295](Backend/BarkCloud.Torrent/Host/TorrentApiService.cs#L295).

**Проблема:** замок внутри `PauseAsync/ResumeAsync` снимается до `_store.SaveChanges()` в API. Параллельные команды могут примениться к движку в одном порядке, а сохранить `Paused` в обратном. Отказ БД после успешного изменения движка также оставляет несогласованность; после рестарта восстановится другое состояние.

**Осталось сделать:** согласовать полную операцию «движок + персистентное состояние» и предусмотреть восстановление после ошибки сохранения. Проверить конкурентные Pause/Resume с задержкой SaveChanges и отказ БД после успешного вызова движка. Остаток установлен по границе блокировки, отдельный тест полной гонки API не запускался.

### F23. Startup принимает ключ, которым HS256 не может подписать JWT — P2

**Где:** [JwtSecret.cs:14](Shared/BarkCloud.Shared.Identity/JwtSecret.cs#L14), [JwtService.cs:49](Backend/BarkCloud.Identity/Services/JwtService.cs#L49), [XAuthExtensions.cs:20](Backend/BarkCloud.GrpcServer/XAuth/XAuthExtensions.cs#L20).

**Проблема:** `MinStartupBytes=16` допускает прежние секреты длиной 16–31 UTF-8 байт. Установленный signer HS256 требует минимум 32 байта. Сервис проходит проверку конфигурации, но выпуск пользовательского токена падает; комментарий о минимальных 128 битах также неверен для фактической подписи.

**Подтверждение:** настоящий `JwtSecret.GetKeyBytes` принял 16-байтовый тестовый секрет; настоящий `JwtService.GenerateUserToken` завершился `ArgumentOutOfRangeException / IDX10720`, 128 бит вместо необходимых 256.

**Осталось сделать:** согласовать startup-проверку с реальным signer, минимально 32 UTF-8 байта. Проверить границу длины через выпуск и валидацию JWT, а не только получение массива ключа.

### F24. CI пропускает тесты потребителей общего кода и Torrent — P2

**Где:** [backend-service-ci.yml:75](.github/workflows/backend-service-ci.yml#L75), [tests.yml:142](.github/workflows/tests.yml#L142), [tests-backend-manual.yml:37](.github/workflows/tests-backend-manual.yml#L37), [build-backend-torrent.yml:27](.github/workflows/build-backend-torrent.yml#L27).

**Проблема:** runtime-фильтр reusable workflow учитывает только сервис, Shared и `rebuild.trigger`; GrpcServer и общие build-файлы отсутствуют. Изменение GrpcServer запускает его собственные тесты, но не тесты зависимых сервисов. Torrent отсутствует в общем/ручном наборе, а его сборка не передаёт test-project. Сейчас в локальном наборе Torrent 26 тестов.

**Осталось сделать:** учитывать общие зависимости при выборе jobs, подключить Torrent.Tests в обычный и ручной CI. Проверить маршрутизацию jobs для изменений GrpcServer, Directory.Build.*, Shared и Torrent. YAML проверен по коду; удалённый CI в аудите не запускался.

### Q03. Крупные классы и повторяющиеся правила усложняют сопровождение — P3

**Где:** [CloudApiEndpoints.cs](Backend/BarkCloud.Web/Endpoints/CloudApiEndpoints.cs), [UnifiedSearchService.cs](Backend/BarkCloud.Files/Services/UnifiedSearchService.cs), [TorrentApiService.cs](Backend/BarkCloud.Torrent/Host/TorrentApiService.cs), [AuthCommandHandler.cs](Backend/BarkCloud.Identity/Features/Auth/AuthCommandHandler.cs).

**Остаток:** CloudApiEndpoints — 1901 строка; TorrentApiService — 441; UnifiedSearchService — 410 плюс 397 строк Sources; AuthCommandHandler — 260. Сохраняются параллельные реализации счётчиков попыток и обработки IP. Разбор нового `x-session-id` также повторяется в XAuthExtensions, UserContext и AuthGateway.

**Осталось сделать:** постепенно выделять самостоятельные сценарии и общие правила при работе над связанными функциями. Проверять, что общая логика имеет одинаковую семантику, прежде чем объединять её. Это технический долг и оценка сопровождаемости, не самостоятельное доказательство runtime-сбоя; полное переписывание классов не требуется.

## Доказательства и границы проверки

| Дополнительная проверка | Наблюдаемый результат |
|---|---|
| F02: остановить refresh после чтения, выполнить reset, продолжить выпуск | JWT старой сессии другого устройства пропущен кэшем |
| F13: остановить rename перед SaveChanges, удалить папку, продолжить | Удалённая папка отсутствует, запись снова живая |
| F15: остановить Attach после чтения, выполнить purge, продолжить | Attach успешен, новая запись есть, оригинала в БД нет; S3 Delete вызван на mock |
| F19: оставить RegisterDevice незавершённым, отменить login | Выдача ждёт RPC до его ручного завершения |
| F20: искать `quarterly report finances` при папке `report` | Папка возвращена с пустым MatchField |
| F23: пройти startup-проверку с 16 байтами, выпустить JWT | Подпись падает с IDX10720 |

Локальные артефакты: [штатный прогон](</private/var/folders/jf/9j0bv8_d5p3dnb68q9d_7ff80000gn/T/barkcloud-reaudit-2pct2g4k/results-575318a/summary.json>), [диагностические результаты](</private/var/folders/jf/9j0bv8_d5p3dnb68q9d_7ff80000gn/T/barkcloud-reaudit-2pct2g4k/diagnostics-575318a/summary.json>). Исходники временных тестов сохранены рядом с диагностическими результатами в `probes/`.

Полный production-прогон Docker/nginx/MinIO, длительная загрузка и нагрузка очереди не выполнялись. PostgreSQL/RabbitMQ, поднятые для аудита, остановлены. Основной вывод о F02/F13/F15 основан на реальных SQL/хендлерах; внешнее удаление S3 в F15 проверено через mock. Остальные статические остатки явно отмечены в соответствующих пунктах.

## Очерёдность

1. F02, F13, F15 — отзыв сессий и целостность файлов.
2. F23, F19 — выпуск токенов, зависание входа и долговечность уведомлений.
3. F17, F18, F21, F22, F20, F24 — длинные операции, очереди, состояние, поиск и CI.
4. Q03 — постепенно вместе с изменениями соответствующих сценариев.

## Standards

Независимая проверка последних 12 коммитов `fc088d6 → 575318a`: **подтверждённых нарушений документированных правил — 0**. Проверены AGENTS.md и соглашения index.md; функциональные изменения описаны в связанных заметках vault, сообщения коммитов соответствуют правилу русского языка.

**Эвристика Duplicated Code, необязательная:** чтение `x-session-id` через `FindFirst` + `long.TryParse` повторяется в [XAuthExtensions.cs:73](Backend/BarkCloud.GrpcServer/XAuth/XAuthExtensions.cs#L73), [UserContext.cs:40](Backend/BarkCloud.GrpcServer/XAuth/UserContext.cs#L40) и [AuthGateway.cs:260](Backend/BarkCloud.Web/Auth/AuthGateway.cs#L260). Общий читатель claim мог бы сократить места изменения правил разбора. Это рекомендация по сопровождению в рамках Q03, функциональной ошибки она не доказывает. Обязательный список оставшихся исправлений этим пунктом расширять не следует.

## Spec

Независимая проверка последних 12 коммитов: **остаются 3 неполных требования на срезе `575318a`**. Ниже самостоятельный вывод Spec-review; его сценарии затем подтверждены дополнительными проверками выше.

1. **F02 — обновление старого refresh может пережить сброс пароля.** Критерий: «после восстановления прежние access/refresh отклоняются». `CreateTokenCommandHandler.cs:19` читает refresh без согласования с отзывом и выпускает JWT на строке 36. Если чтение произошло до reset, а выпуск — после него, токен другого устройства получает `iat > RevokedAt`. `RefreshTokensStorage.cs:86–94` использует `MaxSessionId` только для текущего устройства; для остальных действует временной порог. `TokenRevocationCache.cs:37–42` поэтому пропускает такой JWT даже после загрузки полного feed. Нужно отзывать старые session ID всех устройств либо сериализовать refresh с reset.
2. **F13 — переименование файла может восстановить живую ссылку на удалённую папку.** Критерий: «сериализовать структурные изменения дерева владельца». `RenameFileEntryCommandHandler.cs:38–52` читает запись без `LockTree`; `CloudHierarchyStorage.cs:330–333` вызывает `Update(entry)` и сохраняет все поля. Если между чтением и записью `DeleteDirectory` удалит папку и отправит файл в корзину, rename записывает прежние `DirectoryId` и `IsDeleted=false`. Получается живая запись в отсутствующей папке. Нужны общий замок и чтение внутри него либо обновление только имени с проверкой актуального состояния.
3. **F15 — привязка файла всё ещё расходится с окончательным удалением блоба.** Критерий: «конкурентное добавление ссылки и orphan cleanup не удаляют используемый объект». `AttachFileCommandHandler.cs:68` читает блоб, а на строке 118 отдельно вставляет ссылку. Новый замок дерева не используется в `TrashPurgeService`: строки 103–136 проверяют ссылки и снимают владельца, 226–235 удаляют блоб и S3-объект. Между чтением и вставкой purge может завершиться; внешнего ключа `CloudFileEntry → UploadFile` нет. Нужно согласовать добавление ссылки и захват блоба на удаление.

Scope creep не обнаружен. Итого по осям: Standards — 0 нарушений и 1 необязательная эвристика; Spec — 3 неполных требования, F02 относится к отзыву сессий, F13/F15 — к целостности данных.
