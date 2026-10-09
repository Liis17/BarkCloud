# BarkCloud Builder

Parent: [[Architecture]] · See also: [[Platform/Infrastructure]]

## Назначение

`Tools/BarkCloud.Builder/` — Windows WPF-приложение на .NET 10, которое генерирует конфигурацию запуска BarkCloud: `docker-compose.yml` и `.env`. При выборе nginx дополнительно создаёт конфиг прокси и копирует выбранные сертификаты.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Tools/BarkCloud.Builder/BuilderModel.cs` | `BuilderModel` | Параметры и включение сервисов |
| `Tools/BarkCloud.Builder/BackendComposeGenerator.cs` | `BuildCompose`, `BuildEnv`, `BuildNginxConf` | Генерация compose, env и nginx |
| `Tools/BarkCloud.Builder/MainWindow.xaml.cs` | `OnGenerate` | Запись файлов и копирование сертификатов |
| `Tools/BarkCloud.Builder/BarkCloud.Builder.csproj` | TargetFramework, WPF-UI | Технологическая основа |

## Публичные контракты

| Контракт | Поведение |
|---|---|
| Выходные файлы | Создаёт `docker-compose.yml` и `.env` в выбранной существующей папке |
| Дополнительный nginx | При включённой опции создаёт `nginx/cloud.barkfluff.conf` и `certs/` |
| Канал образа | Release, Nightly или Dev выбирает реестр `docker.barkfluff.com` и суффикс образа |
| Состав сервисов | Configuration, Identity, Users, Files и Web включены всегда; Notification, Torrent, MinIO, RabbitMQ, PostgreSQL, Seq и nginx переключаются в UI |

## Зависимости и взаимодействия

- Модель по умолчанию включает backend-ядро, Web и перечисленные инфраструктурные сервисы, отключает nginx; настройки портов и путей основаны на `Backend/sample.env`.
- Сгенерированный стек использует внешнюю сеть `barkcloud-network`. При включённом nginx наружу маршрутизирует именно он; внутренние сервисы остаются в сети.
- При запуске Builder случайно генерирует `CONFIGURATION_ACCESS_KEY` и `WEB_ADMIN_PASSWORD`.

## Ограничения и важные детали

Папка вывода должна существовать. Файлы записываются UTF-8 без BOM с переводами строк LF. При выборе сертификатов в nginx-конфиг попадают имена файлов, сами сертификаты копируются в `certs/`.

Если `ARCHIVE_TEMP_PATH` пуст, compose использует named volume `archive_temp`. В комментарии `BuilderModel` указано, что такой том создаётся с владельцем root и Files под uid 1654 не сможет записать в него временный архив; для архивов нужна хост-папка с правом записи для uid 1654.
