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
- Образ собирается с `load: true`, затем после паузы `push-delay` теги пушатся по одному; вход в реестр, `docker push` и опрос тегов повторяются до 3 раз через 15 с. `push-delay` растёт на 15 с по порядку файлов `build-backend-*.yml` (configuration 15 … web 105), чтобы одновременные сборки не пушили в реестр разом; новому сервису — следующее значение.
- `paths` в `build-backend-*.yml` перечисляют `Shared/BarkCloud.Shared.*/**` и только нужные `.proto` по принципу сервиса-владельца; `configuration_api.proto` и `session_revocation_api.proto` компилируются в `BarkCloud.GrpcServer` и затрагивают все сервисы.
- Telegram-уведомление шлёт `.github/scripts/send-telegram.sh`: при отказе Markdown повторяет текст без разметки, при неудаче пишет `::warning::` и не валит джобу.
- Для Web CI выполняет `npm ci` и `npm run build` перед `dotnet publish`. Скрипт `npm test` существует, но в текущих workflows вызова Vitest нет.
- Центральный тестовый workflow также содержит Android unit job; отдельные тестовые jobs для iOS и desktop-клиентов в этих workflows не определены.

## Ограничения и важные детали

PostgreSQL-сценарии зависят от отдельной тестовой базы и настройки `BARKCLOUD_TEST_POSTGRES`. В локальном запуске следует передавать строку подключения только к тестовому PostgreSQL; workflow предоставляет её через сервисный контейнер.
