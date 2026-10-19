# Backend — GrpcServer

Parent: [[Index]] · See also: [[Backend/Configuration]], [[Backend/SessionRevocation]], [[Shared/Proto]], [[Shared/SharedLibraries]]

## Назначение

Общая библиотека запуска gRPC-хостов BarkCloud: загрузка настроек, настройка Kestrel, DI/interceptors, JWT middleware, request context, логирование и метрики.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.GrpcServer/WebApplicationBuilderExtensions.cs` | `LoadConfigurationAsync`, `SetRunningAddress` | Bootstrap настроек и Kestrel |
| `Backend/BarkCloud.GrpcServer/LoggingStartupOptions.cs` | `LoggingStartupOptions` | Локальный startup snapshot трёх флагов логирования |
| `Backend/BarkCloud.GrpcServer/SerilogExtensions.cs` | `AddBarkCloudSerilogAsync` | Независимые sinks и общий logging lifecycle |
| `Backend/BarkCloud.GrpcServer/BarkCloudDiagnostics.cs` | `StartOperation` | Контекст фоновой операции и listener штатной передачи trace-контекста |
| `Backend/BarkCloud.GrpcServer/LognaraAgentRuntime.cs` | `LognaraAgentRuntime`, `BarkCloudLoggingLifetime` | Запуск, readiness, мониторинг и остановка принадлежащего хосту агента |
| `Backend/BarkCloud.GrpcServer/ServiceCollectionExtensions.cs` | `AddBarkCloudGrpc` | Регистрация gRPC и interceptor'ов |
| `Backend/BarkCloud.GrpcServer/ServerExceptionInterceptor.cs` | `ServerExceptionInterceptor` | Преобразование ошибок |
| `Backend/BarkCloud.GrpcServer/Tracker/RequestContextInterceptor.cs` | `RequestContextInterceptor` | Контекст входящего запроса |
| `Backend/BarkCloud.GrpcServer/Tracker/SourceIpResolver.cs` | `SourceIpResolver` | Доверенный IP для лимитов |
| `Backend/BarkCloud.GrpcServer/XAuth/XAuthExtensions.cs` | `AddXAuth`, `UseXAuth` | JWT-аутентификация и политики |
| `Backend/BarkCloud.GrpcServer/XAuth/RevocationSyncService.cs` | `RevocationSyncService` | Синхронизация отзывов токенов |

## Публичные контракты

`LoadConfigurationAsync(ServiceId, CancellationToken)` обращается к [[Backend/Configuration]] через `CONFIGURATION_SERVICE_URL` (fallback — `ConfigurationServiceAddr` или localhost). При наличии отправляет `x-config-access-key`. Перед чтением опрашивает `GetReadiness`: первая проверка сразу, максимум 5 проверок с deadline 2 секунды на каждую и четырьмя паузами по 5 секунд. `ready=false`, `Unavailable` и `DeadlineExceeded` приводят к повтору; после пятой неудачи запуск прерывается. Остальные gRPC-ошибки, включая ошибки доступа и `Unimplemented`, прерывают запуск сразу. Диагностика до настройки Serilog выводится в stderr с сервисом, адресом и причиной без bootstrap-ключа только при `ConsoleOutput=true`.

После готовности загрузчик на том же канале с теми же metadata получает global + service settings; для Files отдельно запрашивает S3-профили и группы квот. Чтение настроек и профилей не повторяется при ошибке, снимок добавляется в builder только после успешного получения всех данных. Все шесть потребителей используют общий механизм; отдельного цикла ожидания в Web нет.

`SetRunningAddress` использует `SERVICE_PORT` и `SERVICE_HTTP1PORT` как overrides для `RunSettings`. Kestrel слушает HTTP/2 на `RunSettings:Port`, при заданном `Http1Port` — HTTP/1 на отдельном порту; `RunSettings:Tls` включает TLS.

`AddBarkCloudGrpc` добавляет `ServerExceptionInterceptor` и `RequestContextInterceptor`. `AddXAuth` читает JWT из `x-auth-token`, проверяет подпись/issuer/audience/lifetime; policy `User` принимает user или service token, а `Service` — только service token. Проверка отзывов пользовательских токенов описана в [[Backend/SessionRevocation]].

`RequestContextInterceptor` декодирует Base64 UTF-8 metadata из [[Shared/SharedLibraries]]: имя устройства, ОС, приложение/версию, device ID и client IP. `SourceIpResolver` для лимитов доверяет HTTP `X-Real-IP`, затем адресу TCP-соединения; клиентские `x-ip-address` и `X-Forwarded-For` не используются как доверенный источник.

## Зависимости и взаимодействия

`AddBarkCloudSerilogAsync` во всех семи backend-приложениях независимо управляет Console, Seq и Lognara флагами `ConsoleOutput`, `SeqEnable` и `LognaraEnable` (по умолчанию `true/false/false`; все комбинации, включая `false/false/false`, допустимы). Флаги строго разбираются как boolean и фиксируются из локальной конфигурации до remote overlay; remote значения не меняют startup-поведение. Seq сохраняет свои уровни, enrichers и durable buffer. При включённом Lognara приложение сначала запускает `/usr/local/bin/lognara-agent` без аргументов в том же контейнере, удаляет у дочернего процесса `LOGNARA_SOURCES_FILE` и ждёт до 10 секунд HTTP `GET /v1/logs` с ответом `405` и `Allow: POST`; только затем создаются sinks. Агент принимает Serilog только по `http://127.0.0.1:7400/v1/logs`; standalone sources ему не нужны. Sink Lognara использует ограниченную памятью очередь по умолчанию и не включает отдельный дисковый buffer; постоянный spool принадлежит агенту и отделён от sink. При выключенном Lognara процесс не запускается и runtime не требует бинарный файл. Host останавливается при неожиданном завершении агента. После `RunAsync` host прекращает работу и освобождает DI, затем logging lifetime завершает Serilog flush/dispose максимум за 20 секунд, пока агент ещё запущен; сетевые ожидания Seq ограничены 10 секундами, HTTP-клиент sink Lognara — 5 секундами. Затем агент получает SIGTERM, ожидание ограничено 10 секундами, после чего завершается только принадлежащий приложению процесс. Host shutdown budget — 30 секунд; суммарный предел до принудительного завершения процесса — 60 секунд. `AddBarkCloudMetrics` регистрирует общий `MetricsCollector`; `MetricsReporterService` пишет снимки метрик в структурированный лог каждые 5 секунд.

`configuration_api.proto` и `session_revocation_api.proto` генерируются этим проектом с `GrpcServices="Both"`. Остальные backend-проекты подключают необходимые `.proto` с собственными параметрами генерации. См. [[Shared/Proto]].

## Ограничения и важные детали

### Корреляция логов

После настройки Serilog каждое событие общего pipeline имеет нативные `LogEvent.TraceId` и `SpanId`. Общая обёртка перед разветвлением sinks сохраняет существующие ID, а недостающие генерирует один раз на событие. Поэтому Console, Seq (`@tr`/`@sp`) и Lognara (`trace_id`/`span_id`) получают одинаковую пару; обычные пользовательские свойства с такими именами не заменяют нативные ID. Fallback не меняет `Activity.Current` и не объединяет несвязанные записи одной трассой процесса. Ранний stderr загрузчика Configuration и аварийный вывод после закрытия логгера в эту гарантию не входят.

Logging lifetime включает `ActivityListener` с `PropagationData` для BarkCloud, ASP.NET Core, HTTP/gRPC-клиентов и MassTransit; listener освобождается после flush логгера. Экспорт spans не подключён. HTTP/gRPC и сообщения используют штатную передачу контекста библиотек. `BarkCloudDiagnostics.StartOperation` создаёт W3C Activity: переданный контекст становится родителем, а default начинает независимую корневую операцию. Dispose восстанавливает предыдущую ambient Activity. Фоновые задачи ограничивают операцию одним проходом или заданием; каждый `ServiceMetrics` имеет собственную трассу отчёта, без привязки всех агрегированных запросов к этому ID. Контекст отложенных писем сохраняется отдельно в [[Backend/IdentityNotificationOutbox]].

`ServerExceptionInterceptor` преобразует `BaseGrpcException` в `FailedPrecondition` с trailing metadata `x-error-code`; необработанное исключение становится `Unknown`. Клиентский перехватчик из [[Shared/SharedLibraries]] восстанавливает известные доменные ошибки по коду.
