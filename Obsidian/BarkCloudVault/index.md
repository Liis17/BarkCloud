# BarkCloud — Project Index

> Главная точка входа в документацию проекта. Создано: 2026-05-18

## О проекте

**BarkCloud** — backend и Android-клиент мессенджера/облачного сервиса, построенный на микросервисной архитектуре с gRPC-коммуникацией. Сервер реализован на **.NET (C#)**, мобильный клиент — нативный **Android (Kotlin)**. Контракты между сервисами и клиентом описаны в **Protocol Buffers**.

Архитектура состоит из четырёх независимых микросервисов (`Configuration`, `Identity`, `Users`, `Files`), общих библиотек в `Shared/` и Android-приложения. Инфраструктура (PostgreSQL, RabbitMQ, MinIO, Seq) поднимается через docker-compose.

## Быстрая навигация

### 🏗 Архитектура
- [[structure/overview]] — Общая структура проекта и дерево директорий
- [[structure/entrypoints]] — Точки входа и запуск сервисов
- [[structure/infrastructure]] — Инфраструктура: docker-compose, PostgreSQL, RabbitMQ, MinIO, Seq
- [[structure/testing]] — Юнит-тесты: расположение, стек (xUnit/Moq/FluentAssertions), стратегия мокирования, CI

### 📦 Модули — Backend
- [[modules/backend-configuration]] — Сервис конфигурации (хранит настройки всех микросервисов и зарезервированные юзернеймы)
- [[modules/backend-identity]] — Сервис идентификации (авторизация, токены, 2FA, сессии)
- [[modules/backend-notification]] — Сервис уведомлений (consumer RabbitMQ → SMTP: коды подтверждения, уведомления о входе)
- [[modules/backend-users]] — Сервис пользователей (профили, устройства, контакты, draft-flow)
- [[modules/backend-files]] — Сервис файлов (MinIO, аватары, превью видео через FFmpeg, галерея фото/видео, альбомы) + [[modules/backend-files-cloud]] облачная иерархия папок + [[modules/backend-files-dynamic-folders]] умные (динамические) папки
- [[modules/backend-grpcserver]] — Общий хост для gRPC-серверов (расширения, метрики, перехватчики)
- [[modules/backend-web]] — Веб-клиент (HTTP-страницы для браузера + gRPC-клиент к микросервисам, логин/cookie/JWT)
  - [[modules/web-system-updates]] — Обслуживание: обновление/перезапуск бэкенда из настроек (docker.sock, helper-контейнер, админ-пароль)

### 📦 Модули — Shared
- [[modules/shared-proto]] — Proto-контракты gRPC (общие для backend и клиентов)
- [[modules/shared-auth]] — Interceptors для аутентификации и заголовков (X-Device, X-App и т.д.)
- [[modules/shared-exceptions]] — Кастомные gRPC-исключения по доменам
- [[modules/shared-identity]] — Общие константы Identity (claims, токены, service id)
- [[modules/shared-queue]] — Контракты сообщений для RabbitMQ
- [[modules/shared-securityutilities]] — Утилиты безопасности

### 📱 Модули — Android
- [[modules/android-app]] — Нативный Android-клиент (Kotlin, Compose, Material 3): логин+OTP, 5 табов, локальный файл-браузер, gRPC

### 📱 Модули — iOS
- [[modules/ios-app]] — Нативный iOS-клиент (SwiftUI, Swift 5, iOS 18+), полный паритет с Android — реализован
  - [[modules/ios-background-upload]] — Фоновая загрузка через `URLSession.background` + Live Activity (Lock Screen + Dynamic Island), Share Extension сам грузит, BGTask retry
  - [[modules/ios-widgets]] — Виджеты: Storage (Lock Screen + Control Center), Корзина, Сейф, Недавние фото; deep-link слой `barkcloud://`

### 💻 Модули — Windows (Desktop)
- [[modules/windows-drive]] — Виртуальный диск `X:` поверх облака (Dokany, .NET). Текущее: read-only PoC фазы 2

### 💻 Модули — macOS (Desktop)
- [[modules/macos-drive]] — Папка облака в Finder (NSFileProviderReplicatedExtension, Swift). Каталог `Mac/`. Текущее: код read+write+контейнер компилируется, осталась рантайм-проверка

### 🛠 Инструменты
- [[modules/tools-builder]] — BarkCloud.Builder: WPF-генератор `docker-compose.yml` и `.env` для поднятия бэкенда (WPF-UI, Win11)

### 🛡 Аудит
- `Docs/audit/SECURITY_PERFORMANCE_AUDIT.md` — Пошаговый план аудита безопасности и производительности (Backend + Web + инфраструктура): сквозные этапы E1–E10, проверки по каждому микросервису, нагрузочное тестирование, шаблон отчёта и приложение с известными горячими точками (file:line).
- `Docs/audit/SECURITY_AUDIT_FINDINGS.md` — Отчёт по выполненному аудиту: находки с верификацией по коду (2 Critical, 6 High, 7 Medium, 6 Low), поправки к предварительным находкам, производительность, матрица покрытия и приоритеты ремедиации.
- `Docs/audit/WINDOWS_DRIVE_AUDIT.md` — План аудита Windows-клиента `BarkCloud.Drive` (Engine + App): безопасность (TLS/MITM, IPC named pipe, секреты, данные в `%TEMP%`), производительность (sync-over-async в колбэках Dokany, кэш, резолв путей) и качество кода. Сквозные этапы W1–W8, проверки по компонентам, нагрузочные сценарии, шаблон находок и приложение с горячими точками (file:line). Контекст — [[modules/windows-drive]].
- `Docs/audit/WINDOWS_DRIVE_AUDIT_FINDINGS.md` — Отчёт по выполненному (статически) аудиту Windows-клиента: находки с верификацией по коду (1 Critical, 2 High, 4 Medium, 5 Low), поправки к предварительным находкам, проверенные-корректными места и приоритеты ремедиации. Динамические проверки (MITM, нагрузка, PipeList) помечены как требующие прогона на Windows.
- `Docs/audit/IOS_SECURITY_PERFORMANCE_AUDIT.md` — Пошаговая методология проверки iOS-клиента (приложение + Share Extension + Widgets) по трём осям: безопасность, производительность, качество кода. Сквозные этапы И1–И5, проверки по областям (3.A/3.B/3.C) с якорями на файлы, стресс-сценарии, шаблон отчёта. Только методология (без перечня находок). См. [[modules/ios-app]].

### 🔧 API & gRPC
- [[api/configuration-api]] — gRPC API сервиса Configuration
- [[api/identity-api]] — gRPC API сервиса Identity (Auth, OTP, RefreshToken)
- [[api/users-api]] — gRPC API сервиса Users (профиль, устройства, contacts, draft)
- [[api/users-client-guide]] — Клиентский гайд по Users API (профиль, аватар, имя/юзернейм/bio, приватность, устройства, аккаунт) — для разработки клиента
- [[api/files-api]] — gRPC API сервиса Files (`FilesApi`, `CloudApi`, `FilesServerApi`, `AlbumApi`)
- [[api/files-client-guide]] — Клиентский гайд по Files API (что передать/вернуть: загрузка, галерея, каталоги, альбомы) — для разработки клиента

## Стек технологий

| Слой | Технологии |
|------|-----------|
| Backend | .NET 10 / C#, gRPC, ASP.NET Core, EF Core, PostgreSQL |
| Очереди | RabbitMQ (MassTransit/SDK через `Shared.Queue`) |
| Хранилище файлов | MinIO (S3-совместимое) |
| Логирование | Serilog → Seq |
| Контракты | Protocol Buffers (`.proto` в `Shared/BarkCloud.Proto`) |
| Android | Kotlin, Jetpack Compose, Material 3 (minSdk 30, target 36) |
| iOS | SwiftUI, Swift 5, iOS 18+, grpc-swift 2 |
| Инфраструктура | Docker, docker-compose, nginx (TLS-терминация + gRPC reverse-proxy) |

## Ключевые файлы

| Файл | Назначение |
|------|-----------|
| `BarkCloud.slnx` | Solution-файл .NET (Backend + Shared) |
| `Backend/docker-compose-dev.yml` | Dev-окружение: 4 микросервиса + web + инфра |
| `Backend/nginx/cloud.barkfluff.conf` | Reverse-proxy: TLS-терминация и маршрутизация gRPC по портам |
| `Shared/BarkCloud.Proto/*.proto` | gRPC-контракты между сервисами и клиентом |
| `Android/BarkCloud.Android/build.gradle.kts` | Android-проект (Kotlin DSL) |
| `Ios/BarkCloud/BarkCloud.xcodeproj` | iOS-проект (Xcode, SwiftUI) |
| `Docs/` | Бриф продукта (`WEBSITE_BRIEF.md`), гайдлайны Material 3, аудиты (`Docs/audit/`) |

## Соглашения проекта

- Каждый Backend-микросервис содержит структуру: `Domain/`, `Features/` (CQRS-like), `Host/` (gRPC services), `Persistence/`, `Services/`, `Consumers/`, `Migrations/`.
- Двойные gRPC API: `XxxApi` — клиентский, `XxxServerApi` — серверный/админский. См. `Host/` каждого сервиса.
- Конфигурация сервисов запрашивается из `Configuration`-сервиса при старте (см. `CONFIGURATION_SERVICE_URL`).
- Solution использует формат `.slnx` (новый XML-формат Visual Studio).
