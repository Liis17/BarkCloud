# Architecture

Parent: [[Index]]

## Назначение и границы

BarkCloud — самохостируемое облако файлов и медиа: .NET-микросервисы за nginx, веб-клиент и нативные клиенты (Android, iOS, Windows-диск, macOS File Provider). Клиенты общаются с сервисами по gRPC (контракты в `Shared/BarkCloud.Proto`); бинарные части загрузки/выдачи файлов идут по HTTP.

## Стек

| Технология | Роль | Источник |
|------------|------|----------|
| .NET 10, ASP.NET Core, gRPC, EF Core | Backend-сервисы и общие библиотеки | `BarkCloud.slnx`, `Backend/*/*.csproj` |
| PostgreSQL 18, RabbitMQ (MassTransit), MinIO (S3), Seq (Serilog) | Инфраструктура | `Backend/docker-compose.yml` |
| nginx | TLS-терминация, маршрутизация gRPC/HTTP по портам | `Backend/nginx/cloud.barkfluff.conf` |
| React 18 + Vite + TypeScript | SPA веб-клиента | `Backend/BarkCloud.Web/ClientApp/package.json` |
| Kotlin, Jetpack Compose, grpc-kotlin (OkHttp) | Android-клиент | `Android/BarkCloud.Android/gradle/libs.versions.toml` |
| SwiftUI, grpc-swift | iOS-клиент, виджеты, Share Extension | `Ios/BarkCloud` |
| .NET 10 (Windows) + Dokany / Swift File Provider | Десктопные диски | `Drive/`, `Mac/` |

## Компоненты

| Каталог | Заметка | Роль |
|---------|---------|------|
| `Backend/BarkCloud.Configuration` | [[Backend/Configuration]] | Хранит настройки всех сервисов; остальные получают их при старте |
| `Backend/BarkCloud.Identity` | [[Backend/Identity]] | Вход, токены, 2FA, сессии и их отзыв |
| `Backend/BarkCloud.Users` | [[Backend/Users]] | Профили, устройства, контакты |
| `Backend/BarkCloud.Files` | [[Backend/Files]] | Файлы в S3, медиа, облачная иерархия, загрузки |
| `Backend/BarkCloud.Torrent` | [[Backend/Torrent]] | Скачивание торрентов на хост-диск и импорт в облако |
| `Backend/BarkCloud.Notification` | [[Backend/Notification]] | Потребитель RabbitMQ → письма SMTP |
| `Backend/BarkCloud.GrpcServer` | [[Backend/GrpcServer]] | Общая библиотека хостинга сервисов (не запускается сама) |
| `Backend/BarkCloud.Web` | [[Web/WebApp]] | HTTP-сервер + React SPA; к сервисам ходит как gRPC-клиент |
| `Shared/` | [[Shared/Proto]], [[Shared/SharedLibraries]] | Контракты и общие библиотеки |
| `Android/`, `Ios/` | [[Android/AndroidApp]], [[Ios/IosApp]] | Мобильные клиенты |
| `Drive/`, `Mac/` | [[Desktop/WindowsDrive]], [[Desktop/MacDrive]] | Облако как диск/папка ОС |
| `Tools/BarkCloud.Builder` | [[Tools/Builder]] | WPF-генератор `docker-compose.yml` и `.env` |
| `Tests/` | [[Platform/Testing]] | Тесты backend и shared |

## Основные потоки

### Старт сервиса

Каждый сервис в `Program.cs` вызывает `builder.LoadConfiguration(ServiceId.X)` — настройки запрашиваются у Configuration по `CONFIGURATION_SERVICE_URL`; затем общие расширения из `BarkCloud.GrpcServer` подключают Serilog, метрики и перехватчики. Поэтому все сервисы в compose зависят от `cloud-configuration`. Подробнее: [[Backend/Configuration]], [[Backend/GrpcServer]].

### Клиентский запрос

Клиент → nginx (TLS) → gRPC-сервис по порту: `7020` Identity, `7021` Users, `7025` Files (gRPC + HTTP загрузок), `7027` Torrent; `443` — веб (`cloud-web:8080`) и HTTP-выдача файлов. JWT и заголовки устройства проверяются перехватчиками из `Shared/BarkCloud.Shared.Auth` и `BarkCloud.GrpcServer`. Подробнее: [[Platform/Infrastructure]].

### Межсервисные события

События между сервисами идут через RabbitMQ (MassTransit). Users и Files публикуют их через EF Core outbox MassTransit, Identity фиксирует письмо в собственном outbox в той же локальной транзакции, что и бизнес-операцию, затем фоновый воркер доставляет его; отзыв сессий реплики забирают служебным gRPC-фидом. Подтверждение регистрации остаётся межсервисной последовательностью: Users подтверждает аккаунт до локального коммита Identity, а повтор клиентского запроса восстанавливает локальную сессию при доступном коде. См. [[Backend/AccountDeletionOutbox]], [[Backend/IdentityNotificationOutbox]], [[Backend/SessionRevocation]].

## Соглашения

- Backend-сервис раскладывается на `Domain/`, `Features/` (vertical slices; MediatR в Configuration, Identity, Users, Files), `Host/` (реализации gRPC), `Persistence/` (EF Core, миграции), `Services/`, `Consumers/`, `Infrastructure/`.
- Пары API: `XxxApi` — клиентский, `XxxServerApi` — межсервисный/административный.
- Solution — формат `.slnx`; в него входят Backend, Shared, Tests, Drive и Builder.
- Версия веб-приложения — только `AppVersion.Current` в `Backend/BarkCloud.Web/AppVersion.cs` (переопределяется конфигом `App:Version`).
