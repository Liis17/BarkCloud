# BarkCloud Builder

Parent: [[Architecture]] · See also: [[Platform/Infrastructure]]

## Назначение

`Tools/BarkCloud.Builder/` — Windows WPF-приложение на .NET 10, генерирующее `docker-compose.yml` и `.env`, а также nginx-конфигурацию для встроенного или внешнего reverse proxy.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Tools/BarkCloud.Builder/BuilderModel.cs` | `BuilderModel` | Настройки сервисов, Lognara, Docker-сетей, имён контейнеров и прокси |
| `Tools/BarkCloud.Builder/BuilderModelValidator.cs` | `Validate` | Проверка режима, Lognara, имён, путей сертификатов и портов до записи файлов |
| `Tools/BarkCloud.Builder/BackendComposeGenerator.cs` | `BuildCompose`, `BuildEnv`, `BuildNginxConf` | Генерация compose, env и nginx |
| `Tools/BarkCloud.Builder/MainWindow.xaml.cs` | `OnGenerate` | Проверка, запись файлов и копирование локальных сертификатов |
| `Tools/BarkCloud.Builder/BuilderConfigLoader.cs` | `Load` | Восстановление модели из ранее созданных compose, env и nginx-конфига (кнопка «Открыть») |
| `Tools/BarkCloud.Builder/BarkCloud.Builder.csproj` | TargetFramework, WPF-UI | Технологическая основа |

## Публичные контракты

| Контракт | Поведение |
|---|---|
| Выходные файлы | Создаёт `docker-compose.yml` и `.env` в выбранной существующей папке |
| Открытие конфигурации | «Открыть» требует `docker-compose.yml` и `.env` в папке; значения берутся из `.env`, состав сервисов и канал образа — из compose, домен и пути сертификатов — из `nginx/cloud.barkfluff.conf`. Без nginx-конфига nginx считается выключенным (ошибка, если compose содержит `cloud-nginx`). Папка становится папкой вывода; локальные сертификаты Compose-режима указывают на уже лежащие в `certs/` файлы |
| Режим без nginx | Создаёт только compose и env; Builder показывает предупреждение об отсутствии TLS reverse proxy |
| Compose nginx | Добавляет контейнер `cloud-nginx`, публикует HTTPS/gRPC и выбранные порты консолей, создаёт `nginx/cloud.barkfluff.conf` и копирует выбранные сертификаты в `certs/` |
| Внешний nginx | По умолчанию генерирует `nginx/cloud.barkfluff.conf`, не добавляя nginx-контейнер, публикацию портов или копирование сертификатов |
| Docker-сеть | Переменная `DOCKER_NETWORK_NAME` задаёт реальное имя общей внешней сети; логический ключ Compose остаётся `barkcloud-network` |
| Каналы логирования | Для каждого из семи backend-приложений независимо генерирует `ConsoleOutput`, `SeqEnable` и `LognaraEnable`; начальные значения — `true`, `false`, `false`. Общие переключатели задают канал всем приложениям, после чего можно менять строки по отдельности |
| Lognara | Опционально добавляет relay в общую сеть BarkCloud и настраивает выбранных агентов приложений и включённой инфраструктуры; для каждого агента задаются отдельные token и spool |
| Имена контейнеров | Редактируемые `*_CONTAINER_NAME` задают фактические имена только включённых сервисов; ключи сервисов Compose `cloud-*` и внутренние адреса `CONFIGURATION_SERVICE_URL` остаются стабильными |
| Предпросмотр адресов | В блоках сервисов отображаются выбранные имена контейнеров и внутренние адреса, вычисленные из центральных полей имён и портов; все предпросмотры только для чтения |
| Канал образа | Release, Nightly или Dev выбирает реестр `docker.barkfluff.com` и суффикс образа |
| Порты по умолчанию | Identity 8000, Users 8001, Configuration 8003, Files 8005/8006 и Torrent 8007/8008; стандартные внутренние порты инфраструктуры остаются фиксированными |
| Состав сервисов | Configuration, Identity, Users, Files и Web включены всегда; Notification, Torrent, MinIO, RabbitMQ, PostgreSQL и Seq переключаются в UI |

## Зависимости и взаимодействия

- Модель по умолчанию включает backend-ядро, Web, инфраструктурные сервисы и внешний nginx. Порты и хост-пути основаны на `Backend/sample.env`; стандартные пути данных — `./data/minio`, `./data/pgdata`, `./data/pgbackup`, `./data/seq`, `./data/torrent` и `./data/archive-temp`, относительно папки с генерируемым `docker-compose.yml`.
- Web получает фактические имена Configuration, Identity, Users, Files и Web через `Docker__ContainerNames__*`; Notification и Torrent передаются только при включённых сервисах.
- Каждый backend-сервис получает три независимых параметра логирования из своего набора `*_CONSOLE_OUTPUT`, `*_SEQ_ENABLE` и `*_LOGNARA_ENABLE`. `IncludeSeq` и `IncludeLognara` по умолчанию выключены. Если optional Notification или Torrent выключен, его агент не включается в Compose и его поля не валидируются; значения каналов сохраняются в `.env` для последующего включения сервиса.
- При `IncludeLognara` Builder добавляет `lognara-relay` без опубликованных портов в сеть BarkCloud и настраивает выбранные приложения и включённые сервисы nginx, MinIO, PostgreSQL, RabbitMQ и Seq. Relay отправляет batches на вручную заданный HTTP(S) Core URL, заканчивающийся на `/v1/batches`; его Core token и отдельный token каждого агента задаются вручную. Регистрация агентов автоматически не выполняется. Для включённых инфраструктурных сервисов Builder создаёт рядом с Compose файлы `lognara/*.Dockerfile`; загрузка релиза и проверка `SHA256SUMS` происходят при сборке этих образов. `LOGNARA_AGENT_VERSION` по умолчанию равен `latest` и может быть закреплён тегом релиза.
- Relay и агенты используют внутренний адрес `http://lognara-relay:7401/v1/batches` в той же сети BarkCloud. Пустые пути spool используют отдельные named volumes для каждого агента; старый `lognara_configuration_spool` сохраняется. Bind-пути должны быть доступны на запись uid 1654 для приложений кроме Web, root для Web и инфраструктурных агентов, uid 1000 для relay. При генерации без Lognara relay, обёртки, agent mounts и связанные Compose-настройки не выводятся.
- nginx использует динамическое разрешение Docker DNS через `127.0.0.11`. Нужен nginx OSS 1.27.3 или новее; в общем nginx все upstream используют отдельные shared zones, а TLS session cache имеет имя `barkcloud_ssl`.
- При внешнем nginx установщик подключает его контейнер к выбранной уже созданной Docker-сети и устанавливает сгенерированный файл в `http/conf.d`. Пути сертификата и ключа указываются внутри внешнего контейнера; Builder их не копирует и не перезагружает внешний nginx.
- Seq Console и MinIO Console можно отдельно включить как TLS-прокси на настраиваемых listener-портах. Их backend-порты фиксированы: 80 и 9001 соответственно.
- Порты приложений и дополнительные listener-порты консолей редактируются в разделе «Порты сервисов». Порты MinIO, RabbitMQ, PostgreSQL, Seq и Web показаны там как фиксированные внутренние значения; предпросмотр адреса не меняет внешний `EXTERNAL_*_HOST`.

## Ограничения и важные детали

- `.github/workflows/build-builder.yml` при push в `master` или в pull request с изменениями Builder, его тестов, общих build properties или самого workflow (и при ручном запуске) выполняет тесты и собирает два Release-варианта `win-x64`. Полные папки публикации с `BarkCloud.Builder.exe` упаковываются отдельно: `BarkCloud.Builder-win-x64-self-contained.zip` включает .NET runtime, а `BarkCloud.Builder-win-x64-framework-dependent.zip` требует установленного .NET 10 Desktop Runtime (x64). GitHub Release создаётся только при push или ручном запуске из `master`; pull request выполняет проверки без публикации релиза. Оба архива прикрепляются к одному GitHub Release с тегом `builder-<run_number>-<run_attempt>`, указывающим на собранный коммит. Релиз не получает статус Latest; публикация использует встроенный `GITHUB_TOKEN` с `contents: write`.

- Все проверки модели выполняются до записи compose/env/config. Имена контейнеров должны соответствовать Docker, быть уникальными среди включённых сервисов и не совпадать с `cloud-*` alias другого сервиса, даже если он отключён.
- Папка вывода должна существовать. Файлы записываются UTF-8 без BOM с переводами строк LF. При смене режима существующие `certs/` не удаляются.
- В Compose режиме пустые поля сертификата разрешены, чтобы конфигурацию можно было сгенерировать заранее; для запуска nginx нужны реальные сертификат и ключ в `certs/`.
- Если `ARCHIVE_TEMP_PATH` пуст, compose использует named volume `archive_temp`. Владелец такого тома — root, поэтому Files под uid 1654 не сможет записать туда временный архив; для архивов нужна хост-папка с правом записи для uid 1654.
- До обновления приложения нужно развернуть новый Compose вместе с `.env` и сгенерированными Dockerfile-инструкциями для инфраструктурных агентов, сохранив существующие named volumes spool. Затем обновить образы приложений: новый startup по умолчанию использует только Console, поэтому старый Seq без явных новых флагов отключится. Старый Compose `entrypoint`-wrapper агента приложения нужно убрать, поскольку standalone runtime запускается внутри образа.
- Существующие Configuration и Web получают полный `.env` через `env_file`; token-переменные агентов видны обоим сервисам, поэтому изоляция agent tokens средствами Compose не обеспечивается.
- Агент ограничивает graceful shutdown 10 секундами. Для BarkCloud-контейнеров Builder выставляет grace period 75 секунд, для инфраструктурных агентов — 30 секунд; последний ниже лимита агента и может привести к принудительной остановке при shutdown.
- Seq agent получает только stdout контейнера, без источников внутренних файлов или базы Seq. При Lognara PostgreSQL переводит свои логи на stderr, отключая logging collector. Внешний nginx не оборачивается агентом; его текущие upstream/сертификаты настраиваются независимо.
