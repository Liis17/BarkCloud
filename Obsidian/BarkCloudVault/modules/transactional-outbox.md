# Надёжное удаление аккаунта и транзакции Identity (F11)

Parent: [[modules/backend-users]] · See also: [[modules/backend-identity]] · [[modules/session-revocation]] · [[modules/shared-queue]] · [[structure/testing]]

## Users: изменение и событие

`UsersContext` содержит стандартные `InboxState`, `OutboxState`, `OutboxMessage` MassTransit. Миграция `20261002001157_AddUsersOutbox` создаёт таблицы и индексы. `Program` регистрирует `AddEntityFrameworkOutbox<UsersContext>`, `UsePostgres()` и `UseBusOutbox()`; доставку выполняет штатный hosted service MassTransit 8.5.2, с PostgreSQL-блокировками для нескольких экземпляров.

`DeleteAccountCommandHandler` открывает транзакцию scoped `UsersContext`. `UsersStorage.DeleteUser(userId, ct)` удаляет профиль с каскадом контакта, устройств и приватности; внутренний `SaveChangesAsync` ещё не коммитит внешнюю транзакцию. `UserInfoQueueSender.UserDeletedEvent(userId, ct)` публикует через scoped `IPublishEndpoint` и сохраняет outbox в том же контексте. После commit обработчик увеличивает `accounts_deleted`, `user_events_published`, `user_deleted_published` и возвращает успех. Ошибка публикации/сохранения либо отмена commit откатывает профиль и событие.

gRPC-методы удаления аккаунта и подтверждения сброса пароля передают `ServerCallContext.CancellationToken` в MediatR, далее — в новые транзакции; удаление и публикация `UserDeleted` также используют этот токен.

Событие не имеет TTL. При недоступном RabbitMQ оно остаётся в БД; после восстановления соединения или перезапуска сервиса delivery service отправляет его. Гарантия — как минимум одна доставка: сбой после подтверждения брокера, но до фиксации доставки в БД может дать повтор.

Bus Outbox перехватывает все scoped публикации Users. Поэтому методы `NameChangedEvent`, `UsernameChangedEvent`, `UserChangedAvatarEvent`, `BioChangedEvent` также вызывают `SaveChangesAsync` после публикации. Их существующие изменения профиля и событие по-прежнему могут иметь отдельные коммиты: F11 объединяет именно удаление аккаунта. Метрики с суффиксом `published` теперь означают сохранение для отправки, а не подтверждение RabbitMQ.

## Identity: локальная атомарность

`UserDeletedConsumer` использует один scoped `IdentityContext` для всех хранилищ. В одной явной транзакции выполняет `RevokeAllSessions`, удаление пароля, 2FA-свойств, reset-запросов и кодов. Commit предшествует успешному завершению consumer и `accounts_cleaned_identity`. При исключении всё откатывается; повтор в новом scope может заново прочитать прежние refresh-токены. После успешной обработки повтор не меняет уже сохранённые `RevokedSessions`.

`ConfirmResetPasswordCommandHandler` проверяет ввод и вычисляет новый хеш до открытия транзакции. `TryApprove`, отзыв прежних сессий, запись нового хеша, создание refresh и генерация access выполняются до единственного commit. Ошибка либо отмена возвращает прежний пароль/сессии и не расходует reset. `PasswordChangedNotifier` вызывается после закрытия транзакции; ошибка уведомления не отменяет результат. Конкурентные подтверждения одного reset сохраняют правило одного победителя.

Logout, клиентское и серверное удаление активной сессии сохраняют механизм F10: удаление refresh и вставка долговечного отзыва одним `SaveChangesAsync`. События `SessionRevokedEvent` не возвращаются, дополнительный bus outbox в Identity не нужен. gRPC-вызовы удаления устройства остаются после сохранения отзыва.

## Обновление и диагностика

1. Обновить Identity с транзакционной очисткой и сбросом пароля.
2. Обновить Users; startup `Database.Migrate()` создаст outbox до запуска hosted services. Контракт `UserDeleted` и существующие очереди Identity/Files остаются прежними.
3. Проверить логи MassTransit delivery service, восстановление доставки после перезапуска и уменьшение очереди в БД после восстановления RabbitMQ. Ошибки обработки на стороне consumer проверять также в `user-deleted-identity_error` / `user-deleted-files_error`; исправленный consumer позволяет повторить обработку из error queue.

В БД Users число и возраст ещё ожидающих сообщений:

```sql
SELECT count(*) AS pending_messages,
       now() - min(m."SentTime") AS oldest_message_age
FROM "OutboxMessage" m
JOIN "OutboxState" s ON s."OutboxId" = m."OutboxId"
WHERE s."Delivered" IS NULL;
```

До восстановления брокера рост этих значений ожидаем; после восстановления очередь должна уменьшаться. Не удалять pending-записи и не выполнять `Down` миграции с недоставленными сообщениями. При откате приложения сохранять таблицы до завершения доставки.

Outbox не делает удаление в разных сервисах одной распределённой транзакцией: очистка Identity/Files остаётся асинхронной. Миграция не восстанавливает события, потерянные до F11; сверка прежних осиротевших данных — отдельная операция.

## Проверки

`Users.IntegrationTests` проверяет откат после ошибки публикации/INSERT в outbox, отмену commit, сохранение события при недоступном RabbitMQ, доставку без повторного запроса после восстановления сети и после пересоздания host/DI, доставку всех профильных событий в одном scope и PostgreSQL `Up → Down → Up`.

`Identity.IntegrationTests` проверяет откат и повтор очистки, неизменность долговечных отзывов при повторе, откат reset/пароля/токенов при ошибке записи refresh или отмене commit, повторное использование reset после отката, оба значения `RevokeOtherSessions`, commit до уведомления, гонку двух подтверждений и все три обработчика отзыва на PostgreSQL. См. [[structure/testing]].

Проверено 2026-10-02: полный прогон `BarkCloud.slnx` — 1044 теста, все прошли, без пропусков; в том числе 27 Users integration и 14 Identity integration. Использованы изолированные PostgreSQL 18 и RabbitMQ 4.3.6. EF-проверка обоих контекстов не обнаружила расхождений модели и миграций; PostgreSQL `Up → Down → Up` новой миграции Users сохраняет существующие данные. В CI Users запускается с PostgreSQL 18/RabbitMQ 4.1, Identity — с PostgreSQL 18.
