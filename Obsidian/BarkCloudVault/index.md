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

### 📦 Модули — Backend
- [[modules/backend-configuration]] — Сервис конфигурации (хранит настройки всех микросервисов и зарезервированные юзернеймы)
- [[modules/backend-identity]] — Сервис идентификации (авторизация, токены, 2FA, сессии)
- [[modules/backend-users]] — Сервис пользователей (профили, устройства, контакты, draft-flow)
- [[modules/backend-files]] — Сервис файлов (MinIO, аватары) + [[modules/backend-files-cloud]] облачная иерархия папок
- [[modules/backend-grpcserver]] — Общий хост для gRPC-серверов (расширения, метрики, перехватчики)

### 📦 Модули — Shared
- [[modules/shared-proto]] — Proto-контракты gRPC (общие для backend и клиентов)
- [[modules/shared-auth]] — Interceptors для аутентификации и заголовков (X-Device, X-App и т.д.)
- [[modules/shared-exceptions]] — Кастомные gRPC-исключения по доменам
- [[modules/shared-identity]] — Общие константы Identity (claims, токены, service id)
- [[modules/shared-queue]] — Контракты сообщений для RabbitMQ
- [[modules/shared-securityutilities]] — Утилиты безопасности

### 📱 Модули — Android
- [[modules/android-app]] — Нативный Android-клиент (Kotlin, gradle) — **сейчас пустая заготовка**

### 📱 Модули — iOS
- [[modules/ios-app]] — Нативный iOS-клиент (SwiftUI, Swift 5, iOS 18+), полный паритет с Android — в работе по 6 PR

### 🔧 API & gRPC
- [[api/configuration-api]] — gRPC API сервиса Configuration
- [[api/identity-api]] — gRPC API сервиса Identity (Auth, OTP, RefreshToken)
- [[api/users-api]] — gRPC API сервиса Users (профиль, устройства, contacts, draft)
- [[api/files-api]] — gRPC API сервиса Files (`FilesApi`, `CloudApi`, `FilesServerApi`)

### 📋 Изменения
- [[changelog/2026-05-19]] — iOS PR 1: setup проекта и базовый каркас
- [[changelog/2026-05-18]] — актуализация vault под реальное состояние кода

## Стек технологий

| Слой | Технологии |
|------|-----------|
| Backend | .NET 10 / C#, gRPC, ASP.NET Core, EF Core, PostgreSQL |
| Очереди | RabbitMQ (MassTransit/SDK через `Shared.Queue`) |
| Хранилище файлов | MinIO (S3-совместимое) |
| Логирование | Serilog → Seq |
| Контракты | Protocol Buffers (`.proto` в `Shared/BarkCloud.Proto`) |
| Android | Kotlin, Android SDK (minSdk 35, target 36) |
| Инфраструктура | Docker, docker-compose |

## Ключевые файлы

| Файл | Назначение |
|------|-----------|
| `BarkCloud.slnx` | Solution-файл .NET (Backend + Shared) |
| `Backend/docker-compose-dev.yml` | Dev-окружение: 4 микросервиса + инфра |
| `Shared/BarkCloud.Proto/*.proto` | gRPC-контракты между сервисами и клиентом |
| `Android/BarkCloud.Android/build.gradle.kts` | Android-проект (Kotlin DSL) |

## Соглашения проекта

- Каждый Backend-микросервис содержит структуру: `Domain/`, `Features/` (CQRS-like), `Host/` (gRPC services), `Persistence/`, `Services/`, `Consumers/`, `Migrations/`.
- Двойные gRPC API: `XxxApi` — клиентский, `XxxServerApi` — серверный/админский. См. `Host/` каждого сервиса.
- Конфигурация сервисов запрашивается из `Configuration`-сервиса при старте (см. `CONFIGURATION_SERVICE_URL`).
- Solution использует формат `.slnx` (новый XML-формат Visual Studio).
