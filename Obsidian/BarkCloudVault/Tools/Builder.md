# BarkCloud Builder

Parent: [[Architecture]] · See also: [[Platform/Infrastructure]]

## Назначение

`Tools/BarkCloud.Builder/` — Windows WPF-приложение на .NET 10, генерирующее `docker-compose.yml` и `.env`, а также nginx-конфигурацию для встроенного или внешнего reverse proxy.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Tools/BarkCloud.Builder/BuilderModel.cs` | `BuilderModel` | Настройки сервисов, Docker-сети, имён контейнеров и прокси |
| `Tools/BarkCloud.Builder/BuilderModelValidator.cs` | `Validate` | Проверка режима, имён, путей сертификатов и портов до записи файлов |
| `Tools/BarkCloud.Builder/BackendComposeGenerator.cs` | `BuildCompose`, `BuildEnv`, `BuildNginxConf` | Генерация compose, env и nginx |
| `Tools/BarkCloud.Builder/MainWindow.xaml.cs` | `OnGenerate` | Проверка, запись файлов и копирование локальных сертификатов |
| `Tools/BarkCloud.Builder/BarkCloud.Builder.csproj` | TargetFramework, WPF-UI | Технологическая основа |

## Публичные контракты

| Контракт | Поведение |
|---|---|
| Выходные файлы | Создаёт `docker-compose.yml` и `.env` в выбранной существующей папке |
| Режим без nginx | Создаёт только compose и env; Builder показывает предупреждение об отсутствии TLS reverse proxy |
| Compose nginx | Добавляет контейнер `cloud-nginx`, публикует HTTPS/gRPC и выбранные порты консолей, создаёт `nginx/cloud.barkfluff.conf` и копирует выбранные сертификаты в `certs/` |
| Внешний nginx | По умолчанию генерирует `nginx/cloud.barkfluff.conf`, не добавляя nginx-контейнер, публикацию портов или копирование сертификатов |
| Docker-сеть | Переменная `DOCKER_NETWORK_NAME` задаёт реальное имя общей внешней сети; логический ключ Compose остаётся `barkcloud-network` |
| Имена контейнеров | Редактируемые `*_CONTAINER_NAME` задают фактические имена только включённых сервисов; ключи сервисов Compose `cloud-*` и внутренние адреса `CONFIGURATION_SERVICE_URL` остаются стабильными |
| Предпросмотр адресов | В блоках сервисов отображаются выбранные имена контейнеров и внутренние адреса, вычисленные из центральных полей имён и портов; все предпросмотры только для чтения |
| Канал образа | Release, Nightly или Dev выбирает реестр `docker.barkfluff.com` и суффикс образа |
| Порты по умолчанию | Identity 8000, Users 8001, Configuration 8003, Files 8005/8006 и Torrent 8007/8008; стандартные внутренние порты инфраструктуры остаются фиксированными |
| Состав сервисов | Configuration, Identity, Users, Files и Web включены всегда; Notification, Torrent, MinIO, RabbitMQ, PostgreSQL и Seq переключаются в UI |

## Зависимости и взаимодействия

- Модель по умолчанию включает backend-ядро, Web, инфраструктурные сервисы и внешний nginx. Порты и хост-пути основаны на `Backend/sample.env`; стандартные пути данных — `./data/minio`, `./data/pgdata`, `./data/pgbackup`, `./data/seq`, `./data/torrent` и `./data/archive-temp`, относительно папки с генерируемым `docker-compose.yml`.
- Web получает фактические имена Configuration, Identity, Users, Files и Web через `Docker__ContainerNames__*`; Notification и Torrent передаются только при включённых сервисах.
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
