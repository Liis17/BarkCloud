# BarkCloud — База знаний

Самохостируемое облако файлов и медиа: .NET 10 gRPC-микросервисы, веб-клиент (ASP.NET Core + React), клиенты Android, iOS, Windows и macOS.

## Архитектура

- [[Architecture|Архитектура]] — компоненты, стек, основные потоки, соглашения
- [[Platform/Infrastructure|Инфраструктура]] — docker-compose, nginx, PostgreSQL, RabbitMQ, MinIO, Seq
- [[Platform/Testing|Тестирование]] — тестовые проекты, стек, CI

## Backend

| Заметка | Назначение |
|---------|------------|
| [[Backend/Configuration]] | Настройки всех сервисов, зарезервированные юзернеймы |
| [[Backend/Identity]] | Вход, токены, 2FA, сессии |
| [[Backend/IdentityNotificationOutbox]] | Outbox писем Identity и воркер доставки |
| [[Backend/SessionRevocation]] | Долговечный отзыв сессий и синхронизация реплик |
| [[Backend/Notification]] | RabbitMQ → SMTP |
| [[Backend/Users]] | Профили, устройства, контакты |
| [[Backend/AccountDeletionOutbox]] | Надёжная доставка удаления аккаунта |
| [[Backend/GrpcServer]] | Общий хостинг gRPC-сервисов |
| [[Backend/Files]] | Файлы, медиа, альбомы, API Files |
| [[Backend/FilesCloud]] | Облачная иерархия папок |
| [[Backend/DynamicFolders]] | Умные (динамические) папки |
| [[Backend/ResumableUpload]] | Возобновляемая загрузка частями |
| [[Backend/OriginalLifetime]] | Жизнь оригинала и облачные ссылки |
| [[Backend/ImagePlaceholders]] | Цветовые плейсхолдеры превью |
| [[Backend/Torrent]] | Торренты на хост-диск, стриминг, импорт |

## Web

| Заметка | Назначение |
|---------|------------|
| [[Web/WebApp]] | Веб-клиент: страницы, вход, SPA, версия |
| [[Web/Settings]] | Раздел настроек |
| [[Web/SystemUpdates]] | Обслуживание: обновление и перезапуск бэкенда |
| [[Web/S3Migration]] | Миграция бакетов S3 |
| [[Web/TextViewer]] | Просмотр текстовых файлов |

## Shared и клиентские API

| Заметка | Назначение |
|---------|------------|
| [[Shared/Proto]] | gRPC-контракты |
| [[Shared/SharedLibraries]] | Auth, Exceptions, Identity, Queue, SecurityUtilities |
| [[Api/UsersClientGuide]] | Гайд по Users API для клиентов |
| [[Api/FilesClientGuide]] | Гайд по Files API для клиентов |

## Клиенты

| Заметка | Назначение |
|---------|------------|
| [[Android/AndroidApp]] | Android-приложение |
| [[Android/Authentication]] | Первый запуск и авторизация на Android |
| [[Android/ResumableUpload]] | Возобновляемая загрузка на Android |
| [[Ios/IosApp]] | iOS-приложение |
| [[Ios/BackgroundUpload]] | Фоновая загрузка и Live Activity |
| [[Ios/Widgets]] | Виджеты и deep links |
| [[Ios/FileCache]] | Файловый кеш iOS |
| [[Desktop/WindowsDrive]] | Облако как диск Windows (Dokany) |
| [[Desktop/MacDrive]] | Облако в Finder (File Provider) |
| [[Tools/Builder]] | Генератор `docker-compose.yml` и `.env` |

## Документы вне vault

- `Docs/audit/` — планы и отчёты аудитов безопасности и производительности.
- `Docs/` — бриф продукта, планы фич, гайдлайны Material 3, бренд, прототипы (`Docs/prototypes/`).

## Обновление памяти

Заметки отражают текущее состояние проекта. Обновляй затронутые контракты, потоки, зависимости, ограничения и основания решений; удаляй неверные сведения. Имена файлов и папок — на английском. Changelog и дневники задач не ведутся.
