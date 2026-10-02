# Долговечный отзыв сессий (F10)

Parent: [[modules/backend-identity]] · [[modules/backend-grpcserver]]

## Источник истины

Identity хранит `RevokedSession { Id, UserId, DeviceId, RevokedAt, ExpiresAt }` в таблице `RevokedSessions`. Миграция `20261001194141_AddRevokedSessions` создаёт bigint identity PK и индексы по `RevokedAt` и `ExpiresAt`. Формат JWT и клиентский `identity_api.proto` не менялись.

`RefreshTokensStorage` получает общий scoped `IdentityContext` и `JwtSettings`. `RevokedAt = DateTime.UtcNow` фиксируется один раз на операцию; `ExpiresAt = RevokedAt + ExpiryMinutes + 1 мин`. Удаление refresh-токенов и добавление отзывов выполняются одним `SaveChangesAsync`, в одной транзакции EF. Перед сохранением `ExecuteDeleteAsync` удаляет уже просроченные отзывы; эта очистка независима от транзакции удаления refresh и безопасна при её откате.

| Метод | Поведение | Вызывается |
|---|---|---|
| `RevokeSession(deviceId, userId, ct)` | Удаляет refresh устройства и записывает отзыв; отсутствие refresh → `RefreshTokenNotFoundException` | `RemoveActiveSession`, `RemoveActiveSessionServer` |
| `RevokeSessionSafe(deviceId, userId, ct)` | Записывает отзыв даже без refresh | `Logout` |
| `RevokeAllSessions(userId, exceptDeviceId, ct)` | Удаляет все refresh пользователя, записывает по одному отзыву на уникальное устройство, кроме исключённого; возвращает число устройств | `ConfirmResetPassword` (исключает текущее), `UserDeletedConsumer` (без исключения) |
| `DeleteRefreshTokensByDeviceIdSafe` | Удаляет старые refresh без отзыва access | `SessionIssuer` при повторном входе |

Исключение в сбросе пароля касается только access-отзыва: все прежние refresh, включая текущее устройство, удаляются до выдачи новой пары. Повторная доставка `UserDeleted` не меняет уже сохранённое время отзыва: refresh уже удалены, отзывы остаются. F11 сохраняет сам `UserDeleted` в outbox Users вместе с удалением профиля; очистка Identity и весь сброс пароля дополнительно объединены внешними транзакциями. См. [[modules/transactional-outbox]].

## Синхронизация

[[api/session-revocation-api]] обслуживает сервисные JWT. Identity использует `DbRevocationFeed` (новый DI scope на запрос), остальные сервисы — `GrpcRevocationFeed` с deadline 10 с, `JwtClientInterceptor` и `ExceptionClientInterceptor`.

`RevocationSyncService.StartAsync` загружает полный снимок **после миграций, до Kestrel**. При сбое — до 6 попыток с ожиданиями 1/2/4/8/16 с; исчерпание попыток прерывает запуск, `restart: always` запускает контейнер заново. Ожидание и запросы отменяются токеном остановки. `ExecuteAsync` в .NET 10 сам по себе не блокирует запуск: поэтому начальная загрузка находится в `StartAsync`. Регистрация hosted services последовательная; порядок проверен тестом с реальным Kestrel.

После успешной загрузки сервис ждёт 5 с и запрашивает изменения с `server_time` последнего успешного ответа минус 1 минута. Время ответа Identity снимает перед чтением БД. Перекрытие учитывает задержки коммитов и расхождение часов в пределах этого окна. После ответа — upsert и `Cleanup`; при ошибке — warning, сохранение кэша и курсора, повтор на следующем тике. При доступном Identity обычная задержка применения — около 5 с плюс время запроса; при сетевом сбое она может быть больше.

`TokenRevocationCache.Revoke(userId, deviceId, revokedAt, expiresAt)` сохраняет максимальные `RevokedAt` и `ExpiresAt` для ключа `{userId}:{deviceId}`. Повторы и порядок записей не влияют на результат. `IsRevoked(userId, deviceId, issuedAt)` сохраняет правило `issuedAt <= RevokedAt`. Очистка удаляет именно прочитанную просроченную запись, сохраняя конкурентное обновление.

Users/Files/Torrent получают `IdentityService:Host` и `IdentityService:Token` из каталога Configuration. `EnsureSeedAsync` и `PopulateDefaultsAsync` добавляют их и в существующие развёртывания, сохраняют уже заполненные значения. Web берёт существующий `IdentityService:Host` и генерирует сервисный JWT через `ServiceToken.Generate`. `docker-compose.yml` и генератор Builder добавляют Users/Files/Torrent зависимость от Identity; Identity не ждёт Users при старте.

## Удалённый механизм

`SessionRevokedEvent`, пять `SessionRevokedConsumer` и `TokenRevocationCleanupService` удалены. Web больше не использует MassTransit; другие сервисы сохраняют его для имеющихся сообщений. `UserContext.IssuedAt`, `AuthGateway` и повторная проверка Torrent `StreamProgress` из F09 продолжают проверять тот же кэш. Версия Web — `v1.6.4`.

В RabbitMQ могут остаться очереди `session-revoked-identity/users/files/torrent/web`. Новых публикаций и потребителей нет; оператор может удалить их после обновления **всех** сервисов. Обновлять сначала Configuration и Identity, затем потребителей: новый источник должен появиться до запуска клиентов. Старые экземпляры, использующие только события, требуется перезапустить на новой версии.

## Ограничения

- `iat` имеет секундную точность. Logout и новый вход на том же устройстве в одну секунду могут отклонить новый JWT до истечения отзыва. Риск принят вместо добавления `sid`; зафиксирован тестом `Revoke_SameSecondLogin_RemainsRevokedBecauseJwtIatHasSecondPrecision`.
- Уменьшение `JwtSettings:ExpiryMinutes` может сделать срок отзыва короче жизни ранее выданных JWT; запас 1 мин этого не гарантирует. До истечения прежних JWT сохранять прежний lifetime.
- Снимок не разбит на страницы; объём равен числу ещё действующих отзывов. При росте нагрузки потребуется пагинация.
- БД очищается при очередной операции отзыва, а не по таймеру: без новых операций просроченные строки могут лежать в таблице, но feed их не возвращает.
- При недоступном Identity уже запущенный сервис сохраняет последнее состояние; новый экземпляр не принимает запросы до загрузки снимка.

## Проверено 2026-10-02

`dotnet build BarkCloud.slnx -p:EnableWindowsTargeting=true` проходит на macOS. Без этого флага Windows-проекты solution дают `NETSDK1100`; F10 не добавил предупреждений компилятора и `CS0436` для нового proto. EF `has-pending-model-changes` — изменений нет; на изолированном PostgreSQL проверены миграции up/down/up.

950 тестов Backend/Shared прошли, 7 существующих тестов пропущены; в том числе 20 Users integration на временном PostgreSQL. Новые проверки покрывают откат удаления refresh при ошибке вставки отзыва, исключение текущего устройства, очистку БД, повторную доставку, фильтры feed, ретраи/отмену старта, блокировку Kestrel, дельту с перекрытием, сбой обновления, рестарт кэша и две независимые реплики.

Ручные проверки logout → рестарт Docker-сервисов → 401, две реплики за балансировщиком и недоступный Identity при старте остаются для dev-стенда. В локальном окружении Docker отсутствует; `Backend/docker-compose-dev.yml` в текущем checkout отсутствует. Юнит-тесты с feed проверяют соответствующую логику, но не заменяют этот сетевой прогон.
