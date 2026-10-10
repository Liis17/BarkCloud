# Проверка полученного origin/master — 2026-10-10

Выполнен `git fetch origin`. Проверенный remote-срез: `b4c48bd6dc414f756fdb92ec1de8336365ea82a5`. До получения изменений локальная master содержала отчёт `e4124b76` поверх `2e1a63c3` и отставала от origin на 36 коммитов.

Изменения origin интегрированы в текущую master с сохранением локального отчёта. Единственный конфликт был в report.md: старые сводные статусы заменены повторно проверенными. Production-код, тесты и CI в рабочем дереве совпадают с указанным origin/master; исправления в них этой проверкой не вносились. Пуш не выполнялся.

## Статусы всех 12 пунктов

| Пункт | Статус | Подтверждение |
|---|---|---|
| F02 | Выполнен | Сохранение порога sid по каждому устройству; точная stale refresh/reset регрессия прошла в PostgreSQL-наборе. |
| F13 | Выполнен | Чтение rename под tree lock и сохранение изменённых полей; 24 конкурентные регрессии проходят. |
| F15 | Выполнен | Общие owner locks, проверка ссылок, FK Restrict; 19 конкурентных и 3 миграционных теста проходят. |
| F17 | Выполнен | Два часа на передачу, сквозная отмена Web; Web/Torrent передают настоящий multipart дольше 100 секунд. |
| F18 | Выполнен | aee052e2: scheduled redelivery с durable Quartz/PostgreSQL вместо удержания consumer-слотов. 11 выбранных проверок проходят с настоящим RabbitMQ 4.3.6. |
| F19-A/B | Выполнен | Общие SQL-транзакции бизнес-изменений и outbox, откат при ошибке enqueue; RegisterDevice с deadline 10 секунд и cancellation/WaitAsync. Атомарность и зависший RPC проверены. |
| F20 | Выполнен | Общие SQL-кандидаты ранжирования и подписи; 11 профильных PostgreSQL-тестов и прежняя диагностика F20 проходят. |
| F21 | Выполнен | 356bd2d3: baseline после save, сохранённый снимок/checkpoint; 13 регрессий отказов и неизвестного commit проходят. |
| F22 | Выполнен | 473bf166: gate охватывает актуальное чтение, движок и запись; восстановление/фоновое согласование; 43 профильных сценария проходят. |
| F23 | Выполнен | 1250fa2e: общий MinKeyBytes=32 для UTF-8; границы подписи/валидации проверены настоящим JWT. |
| F24 | Частично выполнен | GrpcServer уже в событиях/фильтрах. Torrent.Tests и общие Directory.Build.props отсутствуют в нужных фильтрах; build gate принимает skipped. |
| Q03 | Не выполнен | CloudApiEndpoints по-прежнему 1904 строки; сценарии загрузок не выделены, повторение claims остаётся. |

Итог: **10 выполнено, 2 открыто**. F19 считается одним пунктом с двумя выполненными подэтапами. Это статус кода основной ветки, а не проверка развёрнутой инфраструктуры или применённых там миграций.

## Результаты запуска

Release, .NET 10, отдельный PostgreSQL 18.6 с locale C.UTF-8. Для длительных передач включён BARKCLOUD_LONG_UPLOAD_TEST=1. У каждого прогона свой artifacts-path и TRX.

| Набор | Прошло | Упало | Пропущено |
|---|---:|---:|---:|
| Files.Tests, весь проект | 642 | 0 | 0 |
| Identity.Tests, весь проект | 349 | 0 | 0 |
| Identity.IntegrationTests, весь проект | 66 | 0 | 0 |
| Torrent.Tests, весь проект | 87 | 0 | 0 |
| Web.Tests, LegacyUploadEndpointsTests | 6 | 0 | 0 |
| GrpcServer.Tests, JwtSecretTests/TokenRevocationCacheTests | 22 | 0 | 0 |
| Files.IntegrationTests, 9 выбранных быстрых проверок | 9 | 0 | 0 |
| Files.IntegrationTests, 2 дополнительных broker-restart сценария | 2 | 0 | 0 |
| **Штатные выбранные проверки** | **1183** | **0** | **0** |
| Отдельная диагностическая проверка F20 | 1 | 0 | 0 |

Первый запуск PostgreSQL-наборов завершился ошибкой настройки тестового подключения: указанный пользователь root отсутствовал в созданном кластере. Подключение исправлено на владельца initdb по effective UID; все затронутые наборы полностью перезапущены. Код не менялся. В таблице указаны итоговые прогоны.

## F18 — проверка исходной проблемы

Production-конфигурация использует UploadProcessingQueue.ConfigureUploadProcessing, ConcurrentMessageLimit=2 и UseScheduledRedelivery с интервалами 10 секунд, 1, 5 и 15 минут. Quartz работает с persistent PostgreSQL store, durable scheduler queue и ScheduledMessageRecoveryListener. Миграция 20261009000000_AddUploadScheduler создаёт необходимые таблицы; Files вызывает Migrate до запуска endpoint. В CI добавлены отдельный Files integration workflow и зависимость публикации Files от этой проверки.

На настоящих локальных PostgreSQL/RabbitMQ прошли неизменённые штатные тесты:

- TwoFailures_ReleaseBothSlotsBeforeFirstProductionRedelivery: оба проблемных pipeline одновременно занимают слоты; после ошибок исправное сообщение завершается за ≤5 секунд до первого production-повтора через 10 секунд. Максимум тяжёлых обработок — два.
- FilesRestart_PreservesRedeliveryAndFiresOverdueTimer: расписание переживает остановку host, повторное применение миграции и рестарт.
- ReadyReplay, CancellationDuringShutdown, IntegrityFailure и Exhaustion_WithFastTestIntervals: идемпотентность, отмена, конечные исходы, освобождение резерва, cleanup, Fault/error queue.
- RabbitRestartDuringWait и TerminalSendFailureWhileRabbitDown: доставка сохраняется после остановки/запуска broker; recovery trigger сохраняет payload и переживает рестарт Files.
- Три проверки RabbitMQ URI и пересоздания тестового host.

Docker в среде отсутствует. Вместо контейнера поднят отдельный нативный RabbitMQ 4.3.6 с собственными nodename, портами и каталогами данных. Для двух restart-сценариев временный адаптер команды docker stop/start управлял только этим экземпляром через rabbitmqctl stop и rabbitmq-server -detached: останавливался и перезапускался сам узел с сохранением его каталога. Test/production-исходники не подменялись. Это проверка RabbitMQ/Quartz, не прогон Docker Compose runner.

Полная цепочка с неизменёнными production-ожиданиями 21м10с не запускалась. Проверка освобождения слотов использовала production-интервалы; проверка исчерпания — штатные сокращённые интервалы. S3/ffmpeg заменены управляемым pipeline, как предусмотрено штатным стендом. Физический MinIO и torrent peer-сеть не поднимались.

Канонический повтор на Docker: bash Tests/Backend/BarkCloud.Files.IntegrationTests/run-f18.sh. Остальные наборы запускаются dotnet test по соответствующим csproj с указанными фильтрами и BARKCLOUD_TEST_POSTGRES на изолированный кластер. Для полного production retry-прогона runner поддерживает BARKCLOUD_F18_PRODUCTION=1.

## Что осталось

F24: добавить общие build-файлы во внешние события и внутренние фильтры, подключить Torrent.Tests к автоматическому/ручному запуску и Torrent CI/CD, требовать success обязательного test-job. Исходный GrpcServer-пробел уже закрыт. GitHub Actions и публикация образов этой проверкой не запускались.

Q03: самостоятельный ограниченный рефакторинг CloudApiEndpoints и повторяющихся правил. Это замечание сопровождения, а не доказанный runtime-дефект.

[Текущий список и задания](../../report.md). [Предыдущая проверка локального среза](backend-recheck-2026-10-10.md) сохранена как история, её открытые статусы F18–F23 не применимы к полученному origin/master.
