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
  TwoWay-binding. Без INotifyPropertyChanged: кнопки «Случайный ключ» и «Обзор…»
  пишут значение прямо в контрол (а оно само уходит в модель).
- **Ядро всегда включено:** `configuration/identity/users/files`. Тумблеры —
  только для `web`, `nginx`, `notification`, `minio`, `rabbitmq`, `postgres`, `seq`.
  На опциональные сервисы никто из ядра не делает `depends_on` → их можно
  включать/исключать без правки зависимостей.
- **Генерация — сборка секций строк** (`BackendComposeGenerator`), не парсинг YAML.
  Сам compose почти не зависит от значений — всё идёт через `${VAR}` из `.env`.
  При всех включённых сервисах вывод **байт-в-байт** совпадает с исходным
  `Backend/docker-compose.yml` (тест на корректность).
- Файлы пишутся в UTF-8 без BOM, переводы строк нормализуются в LF (Docker/Linux).

## Файлы

| Файл | Назначение |
|---|---|
| `BuilderModel.cs` | Все параметры + тумблеры сервисов с дефолтами |
| `BackendComposeGenerator.cs` | `BuildCompose(model)` и `BuildEnv(model)` |
| `MainWindow.xaml(.cs)` | FluentWindow: секции-экспандеры, кнопки «Обзор…» / «Сгенерировать» |

См. также [[structure/infrastructure]] (исходный docker-compose и инфраструктура).
