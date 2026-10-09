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
- Тесты сервиса запускаются и при изменении `Backend/BarkCloud.GrpcServer/**` — это общий хост всех сервисов. SDK во всех .NET-джобах ставится через `actions/setup-dotnet`.
- Сборка backend-образа в `backend-service-ci.yml` зависит от успешного test job. Входы `runtime-paths` (тот же список путей, что `on.push.paths` вызывающего `build-backend-*.yml`) и `push-delay` определяют реакцию на изменения и паузу перед `docker push`.
- Образ собирается без отправки (слои остаются в кэше buildkit-билдера джобы), затем после паузы `push-delay` `docker buildx build --push` пересобирает из кэша и отправляет все теги. Именно buildx, а не `docker push`: демон Docker получал от реестра `401`, хотя buildkit с теми же учётными данными проходит. Вход в реестр, отправка и опрос тегов повторяются до 3 раз через 15 с. `push-delay` растёт на 15 с по порядку файлов `build-backend-*.yml` (configuration 15 … web 105), чтобы одновременные сборки не пушили в реестр разом; новому сервису — следующее значение.
- `paths` в `build-backend-*.yml` перечисляют `Shared/BarkCloud.Shared.*/**` и только нужные `.proto` по принципу сервиса-владельца; `configuration_api.proto` и `session_revocation_api.proto` компилируются в `BarkCloud.GrpcServer` и затрагивают все сервисы.
- Telegram-уведомление шлёт `.github/scripts/send-telegram.sh`: при отказе Markdown повторяет текст без разметки, при неудаче пишет `::warning::` и не валит джобу.
- Для Web CI выполняет `npm ci` и `npm run build` перед `dotnet publish`. Скрипт `npm test` существует, но в текущих workflows вызова Vitest нет.
- Центральный тестовый workflow также содержит Android unit job; отдельные тестовые jobs для iOS и desktop-клиентов в этих workflows не определены.

## Ограничения и важные детали

PostgreSQL-сценарии зависят от отдельной тестовой базы и настройки `BARKCLOUD_TEST_POSTGRES`. В локальном запуске следует передавать строку подключения только к тестовому PostgreSQL; workflow предоставляет её через сервисный контейнер.

## Files: durable upload redelivery

Проект `Tests/Backend/BarkCloud.Files.IntegrationTests/` использует production endpoint,
consumer, processor и artifact cleaner с настоящими PostgreSQL 18/RabbitMQ; подменены
тяжёлый pipeline и физическое удаление блобов. `docker-compose.yml` хранит PostgreSQL
и RabbitMQ в постоянных volumes. Стенд изолирован: тесты удаляют очереди Files и
перезапускают RabbitMQ, поэтому рабочая инфраструктура непригодна.

```bash
bash Tests/Backend/BarkCloud.Files.IntegrationTests/run-f18.sh
BARKCLOUD_F18_PRODUCTION=1 bash Tests/Backend/BarkCloud.Files.IntegrationTests/run-f18.sh \
  --filter FullyQualifiedName~Exhaustion_WithUnchangedProductionIntervals
```

Быстрые сценарии покрывают освобождение двух слотов (здоровое сообщение ≤5 секунд,
до первого production-повтора), рестарты Files и RabbitMQ с сохранёнными данными,
просроченный таймер, сохранение redelivery count, recovery-trigger при окончательной
ошибке отправки, Ready/NoOp до и после рестарта, cancellation, integrity failure,
пять pipeline-ошибок и пять необработанных исключений с `_error`/Fault.

Транспорт может ждать reconnect; для final-error пути тест выключает брокер и
прерывает заблокированный job через `IScheduler.Interrupt`. Проверяет сохранение
payload/headers нового durable trigger до восстановления и доставку после рестарта
host. Это отдельная проверка от обычного восстановления соединения при рестарте брокера.

В тестовом host только быстрый сценарий исчерпания задаёт интервалы 300 мс.
Приёмочная проверка использует исходные 10 секунд, 1, 5 и 15 минут, занимает около
21 минуты 10 секунд плюс обработка; фактическая версия `rabbitmq:latest` печатается
в выводе runner и TRX. CI запускает быстрый прогон через
`.github/workflows/files-upload-integration.yml`, вызываемый PR workflow и Files CI/CD;
последний ждёт успешной интеграционной проверки перед публикацией. Ручной запуск
workflow с `production-intervals` включает полный приёмочный сценарий.

`run-f18.sh` требует Docker Compose и .NET 10, останавливает контейнеры после тестов
и сохраняет volumes. Подробности и прямой запуск через `BARKCLOUD_TEST_POSTGRES`,
`BARKCLOUD_TEST_RABBITMQ`, `BARKCLOUD_TEST_RABBITMQ_CONTAINER` — в README проекта.
Unit-проверка listener (`BarkCloud.Files.Tests/Scheduling/`) подтверждает копирование
payload/headers, задержку 30 секунд и отсутствие нового trigger при успехе/immediate refire.
