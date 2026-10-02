# Долговечный отзыв сессий (F10)

Parent: [[modules/backend-identity]] · [[modules/backend-grpcserver]]

## Источник истины

Identity хранит `RevokedSession { Id, UserId, DeviceId, RevokedAt, ExpiresAt, MaxSessionId? }` в таблице `RevokedSessions`. Миграция `20261001194141_AddRevokedSessions` создаёт bigint identity PK и индексы по `RevokedAt` и `ExpiresAt`; `AddRevokedSessionMaxSessionId` (F02) добавляет nullable bigint `MaxSessionId`. Клиентский `identity_api.proto` не менялся; в access-JWT добавлен клейм `x-session-id` (клиенты JWT не разбирают).

`RefreshTokensStorage` получает общий scoped `IdentityContext` и `JwtSettings`. `RevokedAt = DateTime.UtcNow` фиксируется один раз на операцию; `ExpiresAt = RevokedAt + ExpiryMinutes + 1 мин`. Удаление refresh-токенов и добавление отзывов выполняются одним `SaveChangesAsync`, в одной транзакции EF. Перед сохранением `ExecuteDeleteAsync` удаляет уже просроченные отзывы; эта очистка независима от транзакции удаления refresh и безопасна при её откате.

| Метод | Поведение | Вызывается |
|---|---|---|
| `RevokeSession(deviceId, userId, ct)` | Удаляет refresh устройства и записывает отзыв **по сессии** (`MaxSessionId = max(Id)` удаляемых строк); отсутствие refresh → `RefreshTokenNotFoundException` | `RemoveActiveSession`, `RemoveActiveSessionServer` |
| `RevokeSessionSafe(deviceId, userId, ct)` | То же; если refresh уже нет — запись **по времени** (`MaxSessionId = null`) | `Logout` |
| `RevokeAllSessions(userId, currentDeviceId, ct)` | Удаляет все refresh пользователя. Каждое устройство с refresh-строками — одна запись **по сессии**: `MaxSessionId = max(Id)` его удаляемых строк (текущее — тоже). Возвращает число **прочих** устройств | `ConfirmResetPassword` (с текущим устройством), `UserDeletedConsumer` (без него) |
| `DeleteRefreshTokensByDeviceIdSafe` | Удаляет старые refresh без отзыва access | `SessionIssuer` при повторном входе |

При сбросе пароля все прежние refresh, включая текущее устройство, удаляются до выдачи новой пары; старые access всех устройств отзываются по порогу сессии (см. ниже). Повторная доставка `UserDeleted` не меняет уже сохранённое время отзыва: refresh уже удалены, отзывы остаются. F11 сохраняет сам `UserDeleted` в outbox Users вместе с удалением профиля; очистка Identity и весь сброс пароля дополнительно объединены внешними транзакциями. См. [[modules/transactional-outbox]].

## Отзыв по сессии (F02)

Отзыв по времени (`iat <= RevokedAt`) не отделяет старый access от нового: новый access текущего устройства выдаётся сразу после сброса пароля и может иметь тот же `iat` (секунды), а `RevokedAt` берётся в приложении **до** commit, поэтому access, подписанный уже после commit по старому refresh, получил бы `iat > RevokedAt` и прошёл бы. Access-токен несёт клейм `x-session-id` (`IdentityClaims.SessionId`) — `RefreshToken.Id` сессии, по которой он выдан (`CreateTokenCommandHandler` → `JwtService.GenerateUserToken(userId, deviceId, sessionId)`). Id — монотонный bigint identity, поэтому у новой сессии он строго больше, чем у любых старых строк устройства.

`SaveRevocations` пишет `MaxSessionId = max(Id)` удаляемых refresh **для каждого** отзываемого устройства (не только для текущего при сбросе пароля) — для logout, удаления сессии, сброса пароля и удаления аккаунта. Запись с `MaxSessionId` отзывает токены с `sid <= MaxSessionId` **и токены без sid** (выданы до появления клейма, т.е. раньше отзыва; fail-safe, как для отсутствующего `iat`). Время такой записи порогом не служит — `RevokedAt` нужен только дельте фида. По времени (`iat <= RevokedAt`) отзывается лишь устройство без refresh-строк (повторный `Logout`).

Это закрывает гонку «refresh прочитан до reset, access выпущен после»: `CreateToken` другого устройства прочитал строку (Id=2), reset удалил её и записал `MaxSessionId=2`, access подписан позже с `iat > RevokedAt`, но с `sid=2` — кэш его отклоняет независимо от часов. Проверено на PostgreSQL тестом `ResetPassword_StaleRefreshReadBeforeReset_IssuedAccessIsRevoked` (остановка `CreateToken` после чтения refresh, настоящий reset, фид → кэш). Блокировки между `CreateToken` и reset не нужны.

Контракт: `session_revocation_api.proto` → `optional int64 max_session_id = 5`, `SessionRevocation.MaxSessionId`. `sid` читают `XAuthExtensions` (`OnTokenValidated`), `UserContext.SessionId` (Torrent `StreamProgress`) и Web `AuthGateway` (`WebUser.SessionId`; Web `v1.6.7`).

**Порядок выкладки:** сначала Users/Files/Torrent/Web (читают `sid`, без него работают как раньше), затем **все** реплики Identity. Если сброс пароля выполнится при смешанных версиях Identity, access новой сессии, обновлённый старой репликой (без `sid`), будет отозван до следующего refresh — ограниченное окно.

## Синхронизация

[[api/session-revocation-api]] обслуживает сервисные JWT. Identity использует `DbRevocationFeed` (новый DI scope на запрос), остальные сервисы — `GrpcRevocationFeed` с deadline 10 с, `JwtClientInterceptor` и `ExceptionClientInterceptor`.

`RevocationSyncService.StartAsync` загружает полный снимок **после миграций, до Kestrel**. При сбое — до 6 попыток с ожиданиями 1/2/4/8/16 с; исчерпание попыток прерывает запуск, `restart: always` запускает контейнер заново. Ожидание и запросы отменяются токеном остановки. `ExecuteAsync` в .NET 10 сам по себе не блокирует запуск: поэтому начальная загрузка находится в `StartAsync`. Регистрация hosted services последовательная; порядок проверен тестом с реальным Kestrel.

После успешной загрузки сервис ждёт 5 с и запрашивает изменения с `server_time` последнего успешного ответа минус 1 минута. Время ответа Identity снимает перед чтением БД. Перекрытие учитывает задержки коммитов и расхождение часов в пределах этого окна. После ответа — upsert и `Cleanup`; при ошибке — warning, сохранение кэша и курсора, повтор на следующем тике. При доступном Identity обычная задержка применения — около 5 с плюс время запроса; при сетевом сбое она может быть больше.

Раз в минуту (`FullResyncInterval`, равен overlap) вместо incremental-запроса выполняется **полный снимок**; время последней успешной полной загрузки хранится в сервисе, стартовая загрузка тоже считается. Это закрывает остаток F10: `RevokedAt` ставится до commit (для сброса пароля и `UserDeleted` commit внешней транзакции идёт ещё позже), и отзыв, закоммиченный позже минутного overlap, фильтр `RevokedAt >= changedSince` не вернул бы никогда — до рестарта реплика принимала бы старый access-токен. Теперь такая запись попадает в кэш каждой реплики не позже чем через ~1 мин после commit, пока она не истекла; если commit позже `ExpiryMinutes` после `RevokedAt`, все отзываемые JWT уже недействительны. Неудачная полная сверка повторяется на следующем 5-секундном тике, а не через минуту. Протокол и `DbRevocationFeed` не менялись; `Revoke` идемпотентен, повторные снимки безвредны.

`TokenRevocationCache.Revoke(userId, deviceId, revokedAt, expiresAt, maxSessionId?)` сохраняет для ключа `{userId}:{deviceId}` независимые максимумы порога времени (`RevokedAt`, записи без `MaxSessionId`), порога сессии (`MaxSessionId`) и `ExpiresAt`. Повторы и порядок записей не влияют на результат. `IsRevoked(userId, deviceId, issuedAt, sessionId?)` = `issuedAt <= RevokedAt` **или** (порог сессии задан и (`sessionId` отсутствует или `sessionId <= MaxSessionId`)). Очистка удаляет именно прочитанную просроченную запись, сохраняя конкурентное обновление.

Users/Files/Torrent получают `IdentityService:Host` и `IdentityService:Token` из каталога Configuration. `EnsureSeedAsync` и `PopulateDefaultsAsync` добавляют их и в существующие развёртывания, сохраняют уже заполненные значения. Web берёт существующий `IdentityService:Host` и генерирует сервисный JWT через `ServiceToken.Generate`. `docker-compose.yml` и генератор Builder добавляют Users/Files/Torrent зависимость от Identity; Identity не ждёт Users при старте.

## Удалённый механизм

`SessionRevokedEvent`, пять `SessionRevokedConsumer` и `TokenRevocationCleanupService` удалены. Web больше не использует MassTransit; другие сервисы сохраняют его для имеющихся сообщений. `UserContext.IssuedAt`, `AuthGateway` и повторная проверка Torrent `StreamProgress` из F09 продолжают проверять тот же кэш. Версия Web — `v1.6.4`.

В RabbitMQ могут остаться очереди `session-revoked-identity/users/files/torrent/web`. Новых публикаций и потребителей нет; оператор может удалить их после обновления **всех** сервисов. Обновлять сначала Configuration и Identity, затем потребителей: новый источник должен появиться до запуска клиентов. Старые экземпляры, использующие только события, требуется перезапустить на новой версии.

## Ограничения

- `iat` имеет секундную точность. Отзыв по времени (только устройство без refresh-строк, например повторный logout) и новый вход на том же устройстве в одну секунду могут отклонить новый JWT до истечения отзыва; зафиксировано тестом `Revoke_SameSecondLogin_RemainsRevokedBecauseJwtIatHasSecondPrecision`. Logout с refresh, удаление сессии, сброс пароля и удаление аккаунта отзывают по сессии и этим ограничением не затронуты.
- Запись отзыва живёт `ExpiryMinutes + 1` мин после `RevokedAt`. `CreateToken`, зависший дольше этого окна между чтением refresh и подписью, порогом не покрыт (для gRPC с deadline нереалистично).
- Уменьшение `JwtSettings:ExpiryMinutes` может сделать срок отзыва короче жизни ранее выданных JWT; запас 1 мин этого не гарантирует. До истечения прежних JWT сохранять прежний lifetime.
- Снимок не разбит на страницы; объём равен числу ещё действующих отзывов, и каждая реплика загружает его раз в минуту. При росте нагрузки потребуется пагинация.
- Гарантия «не позже ~1 мин после commit», а не «сразу»: запись, закоммиченная позже overlap, доходит с полной сверкой. Если понадобится мгновенная гарантия — курсор по порядку видимости commit (`xid8` + `pg_snapshot_xmin`) с изменением proto и миграцией; отклонено как избыточное для редкого случая.
- БД очищается при очередной операции отзыва, а не по таймеру: без новых операций просроченные строки могут лежать в таблице, но feed их не возвращает.
- При недоступном Identity уже запущенный сервис сохраняет последнее состояние; новый экземпляр не принимает запросы до загрузки снимка.

## Проверено 2026-10-02

`dotnet build BarkCloud.slnx -p:EnableWindowsTargeting=true` проходит на macOS. Без этого флага Windows-проекты solution дают `NETSDK1100`; F10 не добавил предупреждений компилятора и `CS0436` для нового proto. EF `has-pending-model-changes` — изменений нет; на изолированном PostgreSQL проверены миграции up/down/up.

950 тестов Backend/Shared прошли, 7 существующих тестов пропущены; в том числе 20 Users integration на временном PostgreSQL. Новые проверки покрывают откат удаления refresh при ошибке вставки отзыва, исключение текущего устройства, очистку БД, повторную доставку, фильтры feed, ретраи/отмену старта, блокировку Kestrel, дельту с перекрытием, сбой обновления, рестарт кэша и две независимые реплики.

Ручные проверки logout → рестарт Docker-сервисов → 401, две реплики за балансировщиком и недоступный Identity при старте остаются для dev-стенда. В локальном окружении Docker отсутствует; `Backend/docker-compose-dev.yml` в текущем checkout отсутствует. Юнит-тесты с feed проверяют соответствующую логику, но не заменяют этот сетевой прогон.

### Поздний commit (остаток F10)

Добавлены два юнит-теста `RevocationSyncServiceTests` (запись, закоммиченная позже overlap, доходит с полной сверкой через 60 с модельного времени; сбой сверки повторяется на следующем тике) и интеграционный `RevocationLateCommitTests` на PostgreSQL: настоящий `DbRevocationFeed`, открытая транзакция с `RevokedAt` на 2 минуты старше, промежуточные опросы, commit, incremental не возвращает запись, после полной сверки старый JWT отвергается. Задержка моделируется меткой времени, физически две минуты не ждали. До правки интеграционный тест падал на проверке отзыва после сверки. После: GrpcServer.Tests 59/59, Identity.Tests 309/309, Identity.IntegrationTests 23/23, сборка solution без новых предупреждений.
