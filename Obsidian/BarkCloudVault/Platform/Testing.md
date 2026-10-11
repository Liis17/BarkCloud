# Тестирование

Parent: [[Architecture]] · See also: [[Web/WebApp]] · [[Backend/Files]] · [[Backend/Users]] · [[Shared/SharedLibraries]]

## Назначение

Автоматические проверки сгруппированы по backend-сервисам и общим библиотекам; часть проверок использует настоящие PostgreSQL или RabbitMQ. GitHub Actions выбирает наборы по изменённым путям и запускает сборку образа после успешных тестов сервиса.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Tests/Directory.Build.props` | общие test properties | .NET 10, xUnit, Moq и FluentAssertions |
| `Tests/BarkCloud.TestKit/` | общие test helpers | gRPC-контекст, вызовы и логгер для тестов |
| `.github/workflows/tests.yml` | path filters, test jobs | Выбор тестов на PR и ручном запуске |
| `.github/workflows/backend-service-ci.yml` | test/build jobs | Тесты сервиса перед сборкой и публикацией образа |
| `Backend/BarkCloud.Web/ClientApp/package.json` | npm scripts | Vitest-команда клиентских тестов и сборка SPA |

## Публичные контракты

| Проект/команда | Содержание |
|---|---|
| `Tests/Backend/BarkCloud.*.Tests` | Тесты backend и инфраструктуры по сервисам |
| `Tests/Backend/BarkCloud.Identity.IntegrationTests`, `Tests/Backend/BarkCloud.Users.IntegrationTests` | Отдельные интеграционные проекты |
| `Tests/Shared/*` | Тесты общих библиотек |
| `npm test` в `Backend/BarkCloud.Web/ClientApp` | Запускает Vitest |
| `dotnet test <project.csproj> -c Release` | Запуск указанного .NET test-проекта |

## Зависимости и взаимодействия

- Workflow `tests.yml` запускается для pull request в `dev`, `nightly` и `master`, а также вручную. Изменения в Shared запускают backend-проекты; тесты сервисов выбираются по путям их кода и тестов.
- В CI Files и Users получают PostgreSQL 18 и `BARKCLOUD_TEST_POSTGRES`. Интеграционный workflow Users также запускает RabbitMQ и передаёт `BARKCLOUD_TEST_RABBITMQ`.
- PostgreSQL и RabbitMQ в CI запускает composite action `.github/actions/test-containers` (скрипт `.github/scripts/start-test-containers.sh`: `postgres:18`, `rabbitmq:4.1`), а не `services:` — так `docker pull` повторяется до 5 раз с паузой 20 с × номер попытки: Docker Hub отдаёт таймауты и `toomanyrequests`. Образов `postgres`/`rabbitmq` в `docker.barkfluff.com` нет, поэтому логин `REGISTRY_*` для них не помогает.
- Тесты сервиса запускаются и при изменении `Backend/BarkCloud.GrpcServer/**` — это общий хост всех сервисов. SDK во всех .NET-джобах ставится через `actions/setup-dotnet`.
- Сборка backend-образа в `backend-service-ci.yml` зависит от успешного test job. Входы `runtime-paths` (тот же список путей, что `on.push.paths` вызывающего `build-backend-*.yml`) и `push-delay` определяют реакцию на изменения и паузу перед `docker buildx build --push`.
- Образ собирается без отправки (слои остаются в кэше buildkit-билдера джобы), затем после паузы `push-delay` workflow запускает `docker buildx build --push` для всех тегов. Вход в реестр, команда отправки и опрос тегов повторяются до 3 раз через 15 с; конфигурация описывает попытки, а результат конкретной публикации виден в run workflow. `push-delay` растёт на 15 с по порядку файлов `build-backend-*.yml` (configuration 15 … web 105), чтобы одновременные сборки не пушили в реестр разом; новому сервису — следующее значение.
- `paths` в `build-backend-*.yml` перечисляют `Shared/BarkCloud.Shared.*/**` и только нужные `.proto` по принципу сервиса-владельца; `configuration_api.proto` и `session_revocation_api.proto` компилируются в `BarkCloud.GrpcServer` и затрагивают все сервисы.
- Telegram-уведомление шлёт `.github/scripts/send-telegram.sh`: при отказе Markdown повторяет текст без разметки, при неудаче пишет `::warning::` и не валит джобу.
- Для Web CI выполняет `npm ci` и `npm run build` перед `dotnet publish`. Скрипт `npm test` существует, но в текущих workflows вызова Vitest нет.
- Центральный тестовый workflow также содержит Android unit job; отдельные тестовые jobs для iOS и desktop-клиентов в этих workflows не определены.

## Ограничения и важные детали

PostgreSQL-сценарии зависят от отдельной тестовой базы и настройки `BARKCLOUD_TEST_POSTGRES`. В локальном запуске следует передавать строку подключения только к тестовому PostgreSQL; workflow предоставляет её через контейнер `test-postgres`.

## Files: durable upload redelivery

Источники: Tests/Backend/BarkCloud.Files.IntegrationTests/UploadRedeliveryTests.cs, UploadTestHost.cs, docker-compose.yml и run-f18.sh; реализация: Backend/BarkCloud.Files/Scheduling/UploadProcessingQueue.cs и ScheduledMessageRecoveryListener.cs. Стенд использует PostgreSQL 18 и RabbitMQ с постоянными volumes, удаляет очереди Files и перезапускает broker, поэтому требует изолированных PostgreSQL и RabbitMQ.

```bash
bash Tests/Backend/BarkCloud.Files.IntegrationTests/run-f18.sh
BARKCLOUD_F18_PRODUCTION=1 bash Tests/Backend/BarkCloud.Files.IntegrationTests/run-f18.sh \
  --filter FullyQualifiedName~Exhaustion_WithUnchangedProductionIntervals
```

Быстрый вариант исчерпания использует четыре интервала по 300 мс. ProductionFact Exhaustion_WithUnchangedProductionIntervals использует исходные интервалы 10 секунд, 1, 5 и 15 минут; полный сценарий требует около 21 минуты 10 секунд плюс обработка. TwoFailures_ReleaseBothSlotsBeforeFirstProductionRedelivery оставляет исходные интервалы и проверяет assertion здоровой сессии не позднее 5 секунд, до первого retry через 10 секунд.

Интеграционные тесты используют production endpoint, consumer, UploadSessionProcessor, UploadArtifactCleaner, PostgreSQL и RabbitMQ. ProbePipeline подменяет S3/ffmpeg обработку, а физическая очистка orphan blobs подменена. Одновременно работает один UploadTestHost; рестарты выполняются последовательно. Ключевые сценарии — освобождение слотов, исчерпание обеих цепочек, restart Files с overdue trigger, restart того же RabbitMQ container, сохранение recovery trigger при final send failure, Ready replay, graceful cancellation и integrity failure. Успешный restart подтверждает только исследованный сценарий с тем же сохранённым scheduler/broker state.

UploadTestHost напрямую отправляет ProcessUploadedFile через send endpoint и не регистрирует EF bus outbox. Поэтому suite не проверяет CompleteUploadSession вместе с bus outbox и полной композицией Program.cs. Дополнительный end-to-end тест должен проверять restart и at-least-once delivery с идемпотентным итогом, не обещая exactly-once.

В тестовом host ConsumerStopTimeout равен 1 секунде, StopTimeout — 15 секундам; production Program.cs явно не задаёт эти параметры. CancellationDuringShutdown проверяет graceful cancellation, а TerminalSendFailureWhileRabbitDown прерывает выполняющийся Quartz job через IScheduler.Interrupt; это не hard-kill процесса. Тестовая fixture для необработанного исключения начинается в Uploading и не подтверждает terminal lifecycle multipart-сессии, уже сохранённой в Processing.

Границы scheduler retry и recovery описаны в [[Backend/Files#границы восстановления scheduler]].
`ScheduledMessageRecoveryListenerTests` проверяет копирование payload/headers, trigger
через 30 секунд после final send failure и отсутствие нового trigger при успехе или
immediate refire. Тест использует mock `IScheduler`, а не PostgreSQL Quartz store.

Покрытие не проверяет PostgreSQL outage при записи schedule или recovery trigger, hard-kill Files, параллельную доставку одной SessionId, force-recreate RabbitMQ с постоянным volume, misfire старше 60 секунд или полный Program/outbox путь. Restart RabbitMQ выполняется stop/start того же container; rabbitmq:latest и volume не задают проверенную стабильную hostname/node identity. RabbitMQ использует hostname в node name и по умолчанию в имени каталога данных, поэтому recreate с изменившимся hostname нужно проверять отдельно, включая миграцию существующих данных ([RabbitMQ Clustering Guide](https://www.rabbitmq.com/docs/clustering)). Image digest и production RabbitMQ version этим стендом не устанавливаются.

`run-f18.sh` требует Docker Compose и .NET 10. Перед `up --wait` скрипт повторяет `docker compose pull` до пяти попыток. Версия `rabbitmq:latest` читается командой `docker compose exec -T --user rabbitmq rabbitmq rabbitmqctl version`; root entrypoint образа сохраняется для штатной настройки прав. Healthcheck запускает `rabbitmq-diagnostics -q ping` от пользователя `rabbitmq` через `gosu`. EXIT trap установлен до `up --wait`, сохраняет volumes и возвращает исходный код завершения, даже если `stop` завершился ошибкой. Фактическая версия RabbitMQ печатается runner-командой и acceptance test в TRX. Тест использует `BARKCLOUD_TEST_POSTGRES`, `BARKCLOUD_TEST_RABBITMQ` и `BARKCLOUD_TEST_RABBITMQ_CONTAINER`; `BARKCLOUD_TEST_RABBITMQ` — URI `rabbitmq://...` для MassTransit, а raw RabbitMQ.Client преобразует схему в `amqp`, сохраняя endpoint, credentials и vhost. Инструкции по прямому запуску приведены в `Tests/Backend/BarkCloud.Files.IntegrationTests/README.md`.

`.github/workflows/files-upload-integration.yml` вызывается PR workflow
`.github/workflows/tests.yml` и Files CI/CD workflow
`.github/workflows/build-backend-files.yml`; CI использует быстрый вариант по умолчанию,
а Files CI/CD ждёт успешной интеграционной проверки перед публикацией. Интеграционный
workflow всегда выводит `docker compose logs --no-color` и `docker compose ps --all`,
затем сохраняет TRX artifact. Для ручного запуска полного production-сценария workflow
поддерживает `workflow_dispatch` с `production-intervals=true`. Перед запуском проверь,
что remote `master` соответствует целевому SHA. Команда запуска:
`gh workflow run files-upload-integration.yml --ref master -f production-intervals=true`.

`UploadTestHost` связывает контексты логирования MassTransit и Quartz с `ILoggerFactory` нового host до разрешения bus и scheduler; иначе глобальный контекст остановленного host может ссылаться на закрытую фабрику. Регрессионный `UploadTestHostTests` последовательно создаёт два host и моделирует закрытый Quartz logging provider без сетевых соединений. Один host regression test и два URI-теста `RabbitMqClientConnectionFactoryTests` — всего три проверки — запускаются без Docker.
