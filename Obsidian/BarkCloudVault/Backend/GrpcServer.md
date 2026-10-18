# Backend — GrpcServer

Parent: [[Index]] · See also: [[Backend/Configuration]], [[Backend/SessionRevocation]], [[Shared/Proto]], [[Shared/SharedLibraries]]

## Назначение

Общая библиотека запуска gRPC-хостов BarkCloud: загрузка настроек, настройка Kestrel, DI/interceptors, JWT middleware, request context, логирование и метрики.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Backend/BarkCloud.GrpcServer/WebApplicationBuilderExtensions.cs` | `LoadConfigurationAsync`, `SetRunningAddress` | Bootstrap настроек и Kestrel |
| `Backend/BarkCloud.GrpcServer/ServiceCollectionExtensions.cs` | `AddBarkCloudGrpc` | Регистрация gRPC и interceptor'ов |
| `Backend/BarkCloud.GrpcServer/ServerExceptionInterceptor.cs` | `ServerExceptionInterceptor` | Преобразование ошибок |
| `Backend/BarkCloud.GrpcServer/Tracker/RequestContextInterceptor.cs` | `RequestContextInterceptor` | Контекст входящего запроса |
| `Backend/BarkCloud.GrpcServer/Tracker/SourceIpResolver.cs` | `SourceIpResolver` | Доверенный IP для лимитов |
| `Backend/BarkCloud.GrpcServer/XAuth/XAuthExtensions.cs` | `AddXAuth`, `UseXAuth` | JWT-аутентификация и политики |
| `Backend/BarkCloud.GrpcServer/XAuth/RevocationSyncService.cs` | `RevocationSyncService` | Синхронизация отзывов токенов |

## Публичные контракты

`LoadConfigurationAsync(ServiceId, CancellationToken)` обращается к [[Backend/Configuration]] через `CONFIGURATION_SERVICE_URL` (fallback — `ConfigurationServiceAddr` или localhost). При наличии отправляет `x-config-access-key`. Перед чтением опрашивает `GetReadiness`: первая проверка сразу, максимум 5 проверок с deadline 2 секунды на каждую и четырьмя паузами по 5 секунд. `ready=false`, `Unavailable` и `DeadlineExceeded` приводят к повтору; после пятой неудачи запуск прерывается. Остальные gRPC-ошибки, включая ошибки доступа и `Unimplemented`, прерывают запуск сразу. Диагностика до настройки Serilog выводится в stderr с сервисом, адресом и причиной без bootstrap-ключа.

После готовности загрузчик на том же канале с теми же metadata получает global + service settings; для Files отдельно запрашивает S3-профили и группы квот. Чтение настроек и профилей не повторяется при ошибке, снимок добавляется в builder только после успешного получения всех данных. Все шесть потребителей используют общий механизм; отдельного цикла ожидания в Web нет.

`SetRunningAddress` использует `SERVICE_PORT` и `SERVICE_HTTP1PORT` как overrides для `RunSettings`. Kestrel слушает HTTP/2 на `RunSettings:Port`, при заданном `Http1Port` — HTTP/1 на отдельном порту; `RunSettings:Tls` включает TLS.

`AddBarkCloudGrpc` добавляет `ServerExceptionInterceptor` и `RequestContextInterceptor`. `AddXAuth` читает JWT из `x-auth-token`, проверяет подпись/issuer/audience/lifetime; policy `User` принимает user или service token, а `Service` — только service token. Проверка отзывов пользовательских токенов описана в [[Backend/SessionRevocation]].

`RequestContextInterceptor` декодирует Base64 UTF-8 metadata из [[Shared/SharedLibraries]]: имя устройства, ОС, приложение/версию, device ID и client IP. `SourceIpResolver` для лимитов доверяет HTTP `X-Real-IP`, затем адресу TCP-соединения; клиентские `x-ip-address` и `X-Forwarded-For` не используются как доверенный источник.

## Зависимости и взаимодействия

`AddBarkCloudSerilog` настраивает console и Seq sink. `AddBarkCloudMetrics` регистрирует общий `MetricsCollector`; `MetricsReporterService` пишет снимки метрик в структурированный лог каждые 5 секунд.

`configuration_api.proto` и `session_revocation_api.proto` генерируются этим проектом с `GrpcServices="Both"`. Остальные backend-проекты подключают необходимые `.proto` с собственными параметрами генерации. См. [[Shared/Proto]].

## Ограничения и важные детали

`ServerExceptionInterceptor` преобразует `BaseGrpcException` в `FailedPrecondition` с trailing metadata `x-error-code`; необработанное исключение становится `Unknown`. Клиентский перехватчик из [[Shared/SharedLibraries]] восстанавливает известные доменные ошибки по коду.
