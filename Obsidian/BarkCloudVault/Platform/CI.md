# CI/CD и защита веток

Parent: [[Architecture]] · See also: [[Platform/Testing]] · [[Platform/Infrastructure]] · [[Tools/Builder]]

## Назначение

Один workflow `.github/workflows/ci.yml` проверяет PR, собирает и публикует backend-образы и релизы клиентов, отправляет по одному Telegram-уведомлению на запуск. Схема повторяет родительский проект BarkFluff без ветки `nightly`: разработка идёт в `dev`, релиз — в `master`.

## Источники

| Файл | Роль |
|---|---|
| `.github/workflows/ci.yml` | guard, выбор проверок, агрегат `Tests passed`, сборка, перенос образов, релиз, уведомления |
| `.github/workflows/test-backend.yml` | Матрица .NET test-проектов (Linux) с PostgreSQL/RabbitMQ по потребности |
| `.github/workflows/test-clients.yml` | Windows (Drive build+tests, сборка WPF Builder), Android unit, Swift `Mac/BarkCloudKit` |
| `.github/workflows/test-scripts.yml` | `python3 -m unittest` для `.github/tests` |
| `.github/workflows/files-upload-integration.yml` | Durable upload Files (`run-f18.sh`); вызывается из `ci.yml` и вручную |
| `.github/scripts/pr_tests.py` | Выбор наборов по diff PR, матрица backend, проверка агрегата |
| `.github/scripts/ci_plan.py` | Каталог 8 backend-образов и выбор пересобираемых при push в `dev` |
| `.github/scripts/promote_backend.py` | Копирование dev-образов в master по digest |
| `.github/actions/docker-version/compute-version.sh` | SemVer образа: dev повышает patch, master берёт версию dev `latest` |
| `.github/scripts/ci_notify.py`, `send-telegram.sh`, `.github/actions/ci-result` | Сводное Telegram-сообщение |
| `.github/rulesets/*.json`, `.github/scripts/set-required-tests.ps1` | Правила веток GitHub и их применение |
| `.github/secrets.env.example` | Справочник секретов Actions (без значений) |

## Потоки

- **PR в `dev`** (из любой ветки): `pr_tests.py` сравнивает синтетический merge с базой (`fetch-depth: 2`). Backend-тесты выбираются по графу `.csproj` (папки, `ProjectReference`, linked/embedded файлы), к любому выбору добавляется `BarkCloud.GrpcServer.Tests`. Корневые `*.props`, `Tests/Directory.Build.props`, test-workflow и test-containers запускают все backend-тесты и Drive; неизвестный код в `Backend/`, `Shared/`, `Tests/`, `Tools/` тоже запускает весь backend. Изменение `ci.yml`, `pr_tests.py` или `ci_plan.py` запускает всё.
- **PR в `master`**: принимается только из `dev` этого репозитория (job guard), тесты не повторяются — код прошёл их на пути в `dev`.
- **`Tests passed`**: единственная обязательная проверка; требует успеха каждого выбранного job и пропуска невыбранных.
- **Push в `dev`**: без тестов. Пересобираются только образы, чьи входы изменились (граф `.csproj` + `Directory.Build.props`, `.dockerignore`, `FolderIcons/**` для web, `Backend/rebuild.trigger` и version/push-скрипты для всех). Образ получает метку `org.opencontainers.image.revision`. Собираются Android APK, Drive ZIP и Builder ZIP; публикуется prerelease `v1.x.y-dev.<run_number>.<run_attempt>+<short_sha>`.
- **Push в `master`**: образы не собираются, а копируются из `*-dev` по digest, если dev `latest` отличается от master `latest`. Перенос разрешён, только если коммит образа — предок master и исходники сервиса после него не менялись; иначе сервис падает. Собираются все клиенты, включая IPA и DMG, и публикуется стабильный релиз `v1.x.y`.
- **workflow_dispatch**: `build` собирает всё для ветки, `tests` прогоняет все проверки.

## Ограничения и решения

- Слияние `dev` → `master` только merge-коммитом: перенос образов требует, чтобы коммит dev-образа был предком master; squash это ломает и разводит истории веток. Hotfix-ветки в `master` не принимаются — исправление идёт PR в `dev`, затем продвижение.
- Android `versionCode` = `(major·1e6 + minor·1e3 + patch)·1000 + build`; стабильный build = 999, dev — порядковый номер prerelease этой версии (до 998), поэтому каждая следующая dev-сборка обновляется поверх предыдущей.
- Образы, собранные до перехода на `ci.yml`, не имеют revision-метки: первый перенос в master требует пересобрать dev (`Backend/rebuild.trigger` или dispatch `build`).
- Rulesets: `dev` — PR и строгий `Tests passed`; `master` — PR, `Tests passed`, только merge. Удаление веток и force push запрещены, обход не разрешён никому. `set-required-tests.ps1` применяет их, только когда `ci.yml` уже есть в обеих ветках, и удаляет прежний ruleset `main rules`.
- Concurrency — очередь без отмены (`queue: max`): пропущенный push-запуск потерял бы свой diff.
- Сборка iOS/macOS выполняется только в `master`; PR не проверяет их компиляцию, только Swift-тесты `BarkCloudKit`.
