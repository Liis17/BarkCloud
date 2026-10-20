# Тестирование

Parent: [[Architecture]] · See also: [[Web/WebApp]] · [[Backend/Files]] · [[Backend/Users]] · [[Shared/SharedLibraries]]

## Назначение

Автоматические проверки сгруппированы по backend-сервисам, общим библиотекам и Windows Drive; часть проверок использует настоящие PostgreSQL или RabbitMQ. GitHub Actions выбирает наборы по изменённым путям и запускает сборку образа после успешных тестов сервиса.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Tests/Directory.Build.props` | общие test properties | .NET 10, xUnit, Moq и FluentAssertions |
| `Tests/BarkCloud.TestKit/` | общие test helpers | gRPC-контекст, вызовы и логгер для тестов |
| `Tests/Drive/BarkCloud.Drive.Engine.Tests/` | тесты Windows Drive | Dokany callbacks, outbox/Upload 2.0, сессия, кеш, TLS, настройки и pipe |
| `Mac/BarkCloudKit/Tests/BarkCloudKitTests/` | Swift package tests | Upload 2.0, восстановление частей, отмена, токены и реальные gRPC deadline-сценарии для iOS/macOS сетевого слоя |
| `.github/workflows/tests.yml` | path filters, test jobs | Выбор тестов на PR и ручном запуске; на PR в `master` и при `force-all` — полный прогон, вызывается из `release.yml` через `workflow_call` |
| `.github/workflows/release.yml` | релизный pipeline | Push в `master`: полный прогон тестов → сборка APK/IPA/DMG/Drive ZIP/Builder ZIP → один релиз `v1.x.y` с автоинкрементом |
| `.github/workflows/backend-service-ci.yml` | test/build jobs | Тесты сервиса перед сборкой и публикацией образа |
| `Backend/BarkCloud.Web/ClientApp/package.json` | npm scripts | Vitest-команда клиентских тестов и сборка SPA |

## Публичные контракты

| Проект/команда | Содержание |
|---|---|
| `Tests/Backend/BarkCloud.*.Tests` | Тесты backend и инфраструктуры по сервисам |
| `Tests/Backend/BarkCloud.Identity.IntegrationTests`, `Tests/Backend/BarkCloud.Users.IntegrationTests` | Отдельные интеграционные проекты |
| `Tests/Tools/BarkCloud.Builder.Tests` | Проверки режимов Builder, генерации compose/env/nginx и валидации имён/портов без зависимости от WPF |
| `Tests/Shared/*` | Тесты общих библиотек |
| `Tests/Drive/BarkCloud.Drive.Engine.Tests` | `net10.0-windows`; тесты запускаются в Windows-окружении, unit-сценарии используют фейки вместо сервера и Dokany |
| `npm test` в `Backend/BarkCloud.Web/ClientApp` | Запускает Vitest |
| `dotnet test <project.csproj> -c Release` | Запуск указанного .NET test-проекта |

## Корреляция серверных логов

`BarkCloud.GrpcServer.Tests` проверяет нативные trace/span ID в HTTP payload Seq и Lognara, fallback для событий вне Activity, независимость sinks и shutdown. `BarkCloud.Files.Tests/Tracing/` проверяет реальную цепочку localhost HTTP → gRPC → MassTransit in-memory для sampled/unsampled контекста и сохранение заголовков Files bus outbox в SQLite без доставки. Тесты Identity покрывают сохранение контекста писем и отдельные spans повторных попыток; тесты Web — перенос контекста приватных очередей обслуживания и миграции.

Проверки восстановления Users outbox и Quartz redelivery после перезапуска хостов находятся в соответствующих `BarkCloud.Users.IntegrationTests/OutboxTests.cs` и `BarkCloud.Files.IntegrationTests/UploadRedeliveryTests.cs`. Они требуют отдельных `BARKCLOUD_TEST_POSTGRES` и `BARKCLOUD_TEST_RABBITMQ`; in-memory транспорт и SQLite не заменяют эти проверки durable-доставки. Подробности контракта — [[Backend/GrpcServer]].

## BarkCloud Builder

`Tests/Tools/BarkCloud.Builder.Tests` линкует чистые `BuilderModel`, `BuilderModelValidator` и `BackendComposeGenerator` из `Tools/BarkCloud.Builder/`, поэтому unit-тесты не требуют загрузки WPF. Они проверяют режимы без nginx/Compose/external, пользовательские Docker-имена и сеть, опциональные Seq/MinIO Console proxy, dynamic DNS upstream и отклонение конфликтующих имён и портов.

```bash
dotnet test Tests/Tools/BarkCloud.Builder.Tests -c Release
```

Сгенерированные compose-варианты можно отдельно разобрать без Docker daemon командой `docker compose config --quiet --env-file <temp>/.env`.

## Windows Drive

Проект `Tests/Drive/BarkCloud.Drive.Engine.Tests` нацелен на `net10.0-windows`; тесты запускаются на Windows. Они покрывают файловые операции Dokany через fake file info, ленивую гидрацию и кеш блоков, сохранение и outbox, MoveFile и удаление папок, Upload 2.0, TLS/метаданные, восстановление сессии, ServerKey, logout, настройки и журнал.

- `TestAssemblyInitializer` перенаправляет `engine.log` в системный temp в каталог с PID процесса тестов. `Fakes/TestDirectory` создаёт отдельный временный каталог на каждый тест и удаляет его при завершении.
- `WindowsIpcTests` запускает настоящий named pipe и помечен `[Trait("Category", "WindowsIpc")]`. Этот тест не требует установленного Dokany, но требует Windows.
- `FakeCloud` и `FakeCallInvoker` моделируют Files/Cloud gRPC и состояние сессий загрузки; `FakeHttpHandler` подменяет HTTP-передачу. `FakeDokanFileInfo` моделирует параметры Dokany callback. `FakeTimeProvider` из пакета `Microsoft.Extensions.TimeProvider.Testing` позволяет управлять таймерами и задержками.

Локальный запуск на Windows:

```bash
dotnet build Drive/BarkCloud.Drive.slnx -c Release
dotnet test Tests/Drive/BarkCloud.Drive.Engine.Tests -c Release
dotnet test Tests/Drive/BarkCloud.Drive.Engine.Tests --filter "Category=WindowsIpc"
```

В `.github/workflows/tests.yml` job `Drive (Windows)` запускается на `windows-latest`, когда фильтр `drive` находит изменения в `Drive/**`, `Tests/Drive/**`, `Shared/BarkCloud.Proto/**`, `Directory.Build.props`, `Tests/Directory.Build.props` или самом workflow. Job устанавливает .NET 10, собирает `Drive/BarkCloud.Drive.slnx` в Release и запускает тестовый проект целиком, в том числе категорию `WindowsIpc`.

## Зависимости и взаимодействия

- Workflow `tests.yml` запускается для pull request в `dev` и `master`, а также вручную, по вызову через `workflow_call` (вход `force-all`) из `release.yml`. Изменения в Shared запускают backend-проекты; тесты сервисов выбираются по путям их кода и тестов. PR в `master` всегда запускает полный набор тест-джоб независимо от путей.
- Разработка ведётся в `dev`: рабочие ветки создаются от `dev`, PR направляются в `dev`. В `master` изменения продвигаются только PR из `dev` (или горячих фикс-веток от `master`); merge в `master` публикует релиз через `release.yml`.
- В CI Files и Users получают PostgreSQL 18 и `BARKCLOUD_TEST_POSTGRES`. Интеграционный workflow Users также запускает RabbitMQ и передаёт `BARKCLOUD_TEST_RABBITMQ`.
- PostgreSQL и RabbitMQ в CI запускает composite action `.github/actions/test-containers` (скрипт `.github/scripts/start-test-containers.sh`: `postgres:18`, `rabbitmq:4.1`), а не `services:` — так `docker pull` повторяется до 5 раз с паузой 20 с × номер попытки: Docker Hub отдаёт таймауты и `toomanyrequests`. Образов `postgres`/`rabbitmq` в `docker.barkfluff.com` нет, поэтому логин `REGISTRY_*` для них не помогает.
- Тесты сервиса запускаются и при изменении `Backend/BarkCloud.GrpcServer/**` — это общий хост всех сервисов. SDK во всех .NET-джобах ставится через `actions/setup-dotnet`.
- Сборка backend-образа в `backend-service-ci.yml` зависит от успешного test job. Входы `runtime-paths` (тот же список путей, что `on.push.paths` вызывающего `build-backend-*.yml`) и `push-delay` определяют реакцию на изменения и паузу перед `docker buildx build --push`.
- Образ собирается без отправки (слои остаются в кэше buildkit-билдера джобы), затем после паузы `push-delay` workflow запускает `docker buildx build --push` для всех тегов. Вход в реестр, команда отправки и опрос тегов повторяются до 3 раз через 15 с; конфигурация описывает попытки, а результат конкретной публикации виден в run workflow. `push-delay` растёт на 15 с по порядку файлов `build-backend-*.yml` (configuration 15 … web 105), чтобы одновременные сборки не пушили в реестр разом; новому сервису — следующее значение.
- `paths` в `build-backend-*.yml` перечисляют `Shared/BarkCloud.Shared.*/**` и только нужные `.proto` по принципу сервиса-владельца; `configuration_api.proto` и `session_revocation_api.proto` компилируются в `BarkCloud.GrpcServer` и затрагивают все сервисы.
- Telegram-уведомление шлёт `.github/scripts/send-telegram.sh`: при отказе Markdown повторяет текст без разметки, при неудаче пишет `::warning::` и не валит джобу.
- Для Web CI выполняет `npm ci` и `npm run build` перед `dotnet publish`. Скрипт `npm test` существует, но в текущих workflows вызова Vitest нет.
- Центральный test workflow также содержит Android unit job. Отдельные jobs для iOS и macOS не определены; для Windows Drive есть job `Drive (Windows)`.

## Ограничения и важные детали

PostgreSQL-сценарии зависят от отдельной тестовой базы и настройки `BARKCLOUD_TEST_POSTGRES`. В локальном запуске следует передавать строку подключения только к тестовому PostgreSQL; workflow предоставляет её через контейнер `test-postgres`.

## Apple-клиенты и BarkCloudKit

`Mac/BarkCloudKit/Tests/BarkCloudKitTests` проверяет общий сетевой пакет iOS/macOS: потоковый хеш и части Upload 2.0, восстановление сессии, ошибки и отмену, avatar `READY`-поток, привязку файла и gRPC deadlines с авторизацией. Запускать на macOS с установленным Swift и Xcode:

```bash
swift test --package-path Mac/BarkCloudKit
xcodebuild test -project Ios/BarkCloud/BarkCloud.xcodeproj -scheme BarkCloud \
  -destination 'platform=iOS Simulator,name=iPhone 17'
xcodebuild build -project Mac/BarkCloudDrive/BarkCloudDrive.xcodeproj -scheme BarkCloudDrive \
  -destination 'platform=macOS'
xcodebuild build -project Mac/BarkCloudDrive/BarkCloudDrive.xcodeproj -scheme BarkCloudFS \
  -destination 'platform=macOS'
```

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
