[[index]]

# BarkCloud.Builder — генератор docker-compose и .env

> WPF-приложение (.NET 10, стиль Win11 через **WPF-UI**): визуальный генератор
> `docker-compose.yml` и `.env` для поднятия бэкенда. Каталог `Tools/BarkCloud.Builder/`.
> Начато: 2026-06-12.

## Назначение

Заменяет ручное редактирование `Backend/docker-compose.yml` + `Backend/sample.env`.
Пользователь галочками выбирает состав сервисов и задаёт параметры окружения, на выходе —
два готовых файла в выбранной папке.

## Ключевые решения

- **UI: WPF-UI 4.3.0** (`ui:FluentWindow`, Mica-фон, `CardExpander`/`ToggleSwitch`).
  Тема — системная (`ApplicationThemeManager.ApplySystemTheme()`).
- **Привязка:** `BuilderModel` (POCO с дефолтами из `sample.env`) как `DataContext`,
  TwoWay-binding. Без INotifyPropertyChanged: реактивность — через ElementName-биндинги
  (URL `CONFIGURATION_SERVICE_URL` = `http://cloud-configuration:{порт}` через StringFormat;
  предупреждения-`InfoBar` через `InverseBoolConverter` от тумблеров). Кнопки «Случайно»
  и «Обзор…» пишут значение прямо в контрол.
- **Имена compose-сервисов:** production compose использует единый префикс `cloud-` для
  сервисов и Docker DNS (`cloud-configuration`, `cloud-identity`, `cloud-users`,
  `cloud-files`, `cloud-notification`, `cloud-torrent`, `cloud-web`, `cloud-seq`,
  `cloud-minio`, `cloud-rabbitmq`, `cloud-postgres`).
- **Ядро + web всегда включены:** `configuration/identity/users/files/web` (web отключить
  нельзя). Тумблеры — `nginx`, `notification`, `minio`, `rabbitmq`, `postgres`, `seq`,
  `torrent`; по умолчанию включён `torrent`, а `nginx` выключен — как в
  `Backend/docker-compose.yml`.
  На опциональные сервисы никто из ядра не делает `depends_on` → их можно
  включать/исключать без правки зависимостей.
- **Отключение сервиса убирает его env-переменные** из `.env`; при выключении
  `minio/rabbitmq/postgres/seq` показывается предупреждение (поднять отдельно и
  прописать в базе Configuration). Для `minio/postgres/seq` предупреждение **красное**
  (`Severity=Error`) и их карточка с параметрами **скрывается** (генерировать нечего).
  При выключенном `notification` скрывается карточка «Почта (SMTP)», а блок `EMAIL_*`
  не пишется ни в `.env`, ни в `environment` сервиса `configuration` (письма шлёт
  именно notification). При выключенном `nginx` — отдельное предупреждение: режим без
  TLS (h2c), подключение клиентов может не работать, не тестировался.
- **Без nginx публикуются порты микросервисов:** при выключенном (и дефолтном) тумблере nginx
  `identity/users/files` получают `ports:` наружу (`${IDENTITY_PORT}`, `${USERS_PORT}`,
  `${FILES_PORT}` + `${FILES_HTTP1PORT}` для загрузки/скачивания) — иначе они остаются
  отрезанными от хоста (nginx был единственным мостом). При включённом nginx порты не
  пробрасываются (их публикует и проксирует сам nginx). TLS в этом режиме нет — трафик h2c.
- **Образы:** реестр фиксирован (`docker.barkfluff.com`, read-only), выбор канала
  Release/Dev → `barkcloud-<svc>[-dev]:latest`.
- **Внешние адреса и почта** (карточки UI → `EXTERNAL_*_HOST`, `EMAIL_*` в `.env`): эти env
  читает `configuration` и пишет в БД на чистом старте (см. [[modules/backend-configuration]]).
  Внешние адреса обязательны (дефолт — домен + порт сервиса), SMTP опционален (пусто → без почты);
  карточка SMTP видна только при включённом `notification`.
- **Папка временных архивов** (`ARCHIVE_TEMP_PATH` → том `/mnt/archive-temp` сервиса `files`):
  карточка «Файлы — временные архивы», всегда видна (files — ядро). Пусто → named volume
  `archive_temp`, который Docker создаёт от root, а `files` работает под uid 1654 и не может
  туда писать → `Access denied` при скачивании папок/альбомов архивом. Решение — указать
  внешнюю папку с правами на запись для uid 1654 (как `MINIO_DATA_PATH` и пр.).
- **Nginx (при включённом тумблере):** в выходную папку пишутся `nginx/cloud.barkfluff.conf`
  (генерируется из шаблона: `server_name`/домен, listen+upstream порты, имена файлов
  сертификатов подставляются из полей) и папка `certs/` (выбранные crt/key копируются туда
  под их именами). Секция UI видна только когда nginx включён; при невыбранных сертификатах —
  предупреждение (можно продолжить без HTTPS, небезопасно). Дефолты дают конфиг, **байт-в-байт**
  совпадающий с `Backend/nginx/cloud.barkfluff.conf`.
- **Генерация — сборка секций строк** (`BackendComposeGenerator`), не парсинг YAML.
  Сам compose почти не зависит от значений — всё идёт через `${VAR}` из `.env`.
  При дефолтных настройках (канал Release, `torrent` включён, `nginx` выключен) вывод
  соответствует `Backend/docker-compose.yml`; альтернативный режим с nginx сохраняется
  для совместимости.
- Файлы пишутся в UTF-8 без BOM, переводы строк нормализуются в LF (Docker/Linux).

## Файлы

| Файл | Назначение |
|---|---|
| `BuilderModel.cs` | Все параметры + тумблеры сервисов с дефолтами |
| `BackendComposeGenerator.cs` | `BuildCompose(model)`, `BuildEnv(model)`, `BuildNginxConf(model)` |
| `InverseBoolConverter.cs` | Инверсия bool для показа предупреждений при выключенном тумблере |
| `AnyEmptyToBoolConverter.cs` | MultiValue: предупреждение о сертификатах, если хоть одно поле пусто |
| `MainWindow.xaml(.cs)` | FluentWindow: секции-экспандеры, выбор сертификатов, запись nginx/+certs/ |

См. также [[structure/infrastructure]] (исходный docker-compose и инфраструктура).
