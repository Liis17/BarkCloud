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
| `Backend/BarkCloud.Transcoder` | [[Backend/VideoVariants]] | Изолированный FFmpeg sidecar для AV1/WebM |
| `Backend/BarkCloud.Torrent` | [[Backend/Torrent]] | Скачивание торрентов на хост-диск и импорт в облако |
| `Backend/BarkCloud.Notification` | [[Backend/Notification]] | Потребитель RabbitMQ → письма SMTP |
| `Backend/BarkCloud.GrpcServer` | [[Backend/GrpcServer]] | Общая библиотека хостинга сервисов (не запускается сама) |
| `Backend/BarkCloud.Web` | [[Web/WebApp]] | HTTP-сервер + React SPA; к сервисам ходит как gRPC-клиент |
| `Shared/` | [[Shared/Proto]], [[Shared/SharedLibraries]] | Контракты и общие библиотеки |
| `Android/`, `Ios/` | [[Android/AndroidApp]], [[Ios/IosApp]] | Мобильные клиенты |
| `Drive/`, `Mac/` | [[Desktop/WindowsDrive]], [[Desktop/MacDrive]] | Облако как диск/папка ОС |
| `Tools/BarkCloud.Builder` | [[Tools/Builder]] | WPF-генератор `docker-compose.yml` и `.env` |
| `Tests/` | [[Platform/Testing]] | Тесты backend, shared и Windows Drive (`Tests/Drive/BarkCloud.Drive.Engine.Tests`); Android unit-тесты — в `Android/BarkCloud.Android` (job в `tests.yml`) |

## Основные потоки

### Старт сервиса

Потребители Configuration в `Program.cs` вызывают `await builder.LoadConfigurationAsync(ServiceId.X)` — сначала опрашивают внутренний `GetReadiness` по `CONFIGURATION_SERVICE_URL`, затем получают настройки. Общий загрузчик делает максимум 5 проверок с deadline 2 секунды и интервалами 5 секунд; после исчерпания попыток запуск прерывается. Configuration начинает слушать сеть только после успешных миграций и заполнения настроек. Затем общие расширения из `BarkCloud.GrpcServer` подключают Serilog, метрики и перехватчики. Все backend-приложения в compose, кроме изолированного `cloud-transcoder`, зависят от `cloud-configuration`; ожидание его готовности выполняется приложениями. Подробнее: [[Backend/Configuration]], [[Backend/GrpcServer]]. Поток транскодирования видео описан в [[Backend/VideoVariants]].

### Каналы логирования

Семь backend-приложений — Configuration, Identity, Users, Files, Notification, Torrent и Web — читают локальные startup flags до `LoadConfigurationAsync`. `ConsoleOutput=true`, `SeqEnable=false` и `LognaraEnable=false` по умолчанию; CLI имеет приоритет над environment. Настройки console, Seq и Lognara независимы и задаются по отдельности для каждого приложения. При включении Lognara локальный relay в той же Docker-сети пересылает batches в настроенный вручную Core endpoint `/v1/batches`; агентские tokens и регистрация управляются отдельно от приложения. См. [[Tools/Builder]] и [[Platform/Infrastructure]].

### Клиентский запрос

Общий Serilog pipeline гарантирует нативные trace/span ID каждому событию всех семи приложений. Активный контекст передаётся штатными механизмами HTTP/gRPC и MassTransit; фоновые операции создают отдельные ограниченные scopes, а одиночные события получают собственные ID. Outbox писем Identity сохраняет W3C-контекст в БД для доставки после перезапуска. Контракт и исключения для раннего stderr описаны в [[Backend/GrpcServer]], durable-контекст писем — в [[Backend/IdentityNotificationOutbox]].

Клиент → nginx (TLS) → gRPC-сервис по порту: `8000` Identity, `8001` Users, `8005` Files (gRPC + HTTP загрузок), `8007` Torrent; `443` — веб (`cloud-web:8080`) и HTTP-выдача файлов. JWT и заголовки устройства проверяются перехватчиками из `Shared/BarkCloud.Shared.Auth` и `BarkCloud.GrpcServer`. Подробнее: [[Platform/Infrastructure]].

### Межсервисные события

События между сервисами идут через RabbitMQ (MassTransit). Users и Files публикуют их через EF Core outbox MassTransit, Identity фиксирует письмо в собственном outbox в той же локальной транзакции, что и бизнес-операцию, затем фоновый воркер доставляет его; отзыв сессий реплики забирают служебным gRPC-фидом. Подтверждение email-регистрации вызывает Users до локального коммита Identity. При первичной парольной регистрации с отключённой почтой Identity сначала фиксирует пароль и refresh, а верный пароль позволяет повторно завершить draft после сбоя Users. См. [[Backend/AccountDeletionOutbox]], [[Backend/IdentityNotificationOutbox]], [[Backend/SessionRevocation]].

### Секреты и границы доверия

PostgreSQL и RabbitMQ используют отдельные service-role accounts: сервисы с БД подключаются к собственной базе, а каждый backend получает отдельного брокерного пользователя с правами на свои события и очереди. Configuration остаётся доверенным источником настроек и получает индивидуальные пароли этих сервисов из явных environment-переменных; Compose не передаёт ему bootstrap/admin credentials PostgreSQL и RabbitMQ. Web остаётся доверенным администратором: контейнер запускается от root, получает полный `.env` и Docker socket, поэтому его компрометация не изолирована от инфраструктуры. Provisioning и переход существующих установок описаны в [[Platform/Infrastructure]].

### Вход в Web

`BarkCloud.Web` отдаёт auth React entry через отдельную HTML-оболочку; Identity остаётся источником учётных данных, 2FA-кодов и reset-кодов. Web хранит продолжение browser flow в ограниченном process-local кеше, привязанном к device cookie, и защищает формы antiforgery. Регистрация использует атомарные Identity RPC с первоначальным паролем; публичный контракт и ограничения потока описаны в [[Web/WebApp]].

## Соглашения

- Backend-сервис раскладывается на `Domain/`, `Features/` (vertical slices; MediatR в Configuration, Identity, Users, Files), `Host/` (реализации gRPC), `Persistence/` (EF Core, миграции), `Services/`, `Consumers/`, `Infrastructure/`.
- Пары API: `XxxApi` — клиентский, `XxxServerApi` — межсервисный/административный.
- Solution — формат `.slnx`; в него входят Backend, Shared, Tests, Drive и Builder.
- Версия веб-приложения — только `AppVersion.Current` в `Backend/BarkCloud.Web/AppVersion.cs` (переопределяется конфигом `App:Version`).
