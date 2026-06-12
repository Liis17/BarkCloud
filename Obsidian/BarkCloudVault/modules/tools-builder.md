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
- **Ядро + web всегда включены:** `configuration/identity/users/files/web` (web отключить
  нельзя). Тумблеры — только `nginx`, `notification`, `minio`, `rabbitmq`, `postgres`, `seq`.
  На опциональные сервисы никто из ядра не делает `depends_on` → их можно
  включать/исключать без правки зависимостей.
- **Отключение сервиса убирает его env-переменные** из `.env`; при выключении
  `minio/rabbitmq/postgres/seq` показывается предупреждение (поднять отдельно и
  прописать в базе Configuration).
- **Образы:** реестр фиксирован (`docker.barkfluff.com:5000`, read-only), выбор канала
  Release/Dev → `barkcloud-<svc>[-dev]:latest`.
- **Генерация — сборка секций строк** (`BackendComposeGenerator`), не парсинг YAML.
  Сам compose почти не зависит от значений — всё идёт через `${VAR}` из `.env`.
  При всех включённых сервисах и канале Release вывод **байт-в-байт** совпадает с исходным
  `Backend/docker-compose.yml` (тест на корректность).
- Файлы пишутся в UTF-8 без BOM, переводы строк нормализуются в LF (Docker/Linux).

## Файлы

| Файл | Назначение |
|---|---|
| `BuilderModel.cs` | Все параметры + тумблеры сервисов с дефолтами |
| `BackendComposeGenerator.cs` | `BuildCompose(model)` и `BuildEnv(model)` |
| `InverseBoolConverter.cs` | Инверсия bool для показа предупреждений при выключенном тумблере |
| `MainWindow.xaml(.cs)` | FluentWindow: секции-экспандеры, кнопки «Обзор…» / «Сгенерировать» |

См. также [[structure/infrastructure]] (исходный docker-compose и инфраструктура).
