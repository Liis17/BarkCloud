# F18 — durable upload redelivery

Изолированный стенд PostgreSQL 18 + RabbitMQ с постоянными Docker volumes. Не направлять
тесты на рабочую инфраструктуру: они удаляют четыре очереди Files перед каждым сценарием,
создают отдельные временные базы и останавливают контейнер RabbitMQ.

## Запуск

Из корня репозитория, с Docker Compose и .NET SDK 10:

```bash
bash Tests/Backend/BarkCloud.Files.IntegrationTests/run-f18.sh
```

Быстрый прогон сокращает четыре интервала до 300 мс только в тестовом host для проверки
исчерпания. Сценарий освобождения слотов использует production-интервалы: исправное
сообщение завершается за ≤5 секунд после двух исключений, до первого повтора через 10 секунд.
Оба неисправных pipeline одновременно занимают два слота перед освобождением.

Приёмочный прогон полной цепочки с неизменёнными интервалами 10 секунд, 1, 5 и 15 минут:

```bash
BARKCLOUD_F18_PRODUCTION=1 bash Tests/Backend/BarkCloud.Files.IntegrationTests/run-f18.sh \
  --filter FullyQualifiedName~Exhaustion_WithUnchangedProductionIntervals
```

Два сообщения одновременно проверяют пять ошибок pipeline (`Failed`) и пять
необработанных исключений (`process-uploaded-file_error` и `Fault<ProcessUploadedFile>`).
Время ожиданий — 21 минута 10 секунд плюс обработка. Версия RabbitMQ (`rabbitmq:latest`,
как в production compose) печатается runner и сохраняется в выводе/TRX. Этот прогон
доступен вручную в `.github/workflows/files-upload-integration.yml` через
`production-intervals`. PR CI и Files CI/CD запускают быстрые сценарии; job публикации
образа Files зависит от успешной интеграционной проверки. Фактический результат отправки
образа определяется шагом `docker buildx build --push` в workflow.

Runner использует порты `55418` и `56718` и compose project `barkcloud-f18` (можно
переименовать через `BARKCLOUD_F18_PROJECT`). Одновременно запускать только один стенд.
Проверка версии RabbitMQ выполняется через `docker compose exec -T --user rabbitmq`;
entrypoint контейнера по-прежнему запускается от root для штатной настройки прав.
Healthcheck запускает `rabbitmq-diagnostics -q ping` от пользователя `rabbitmq` через
`gosu`. Cleanup trap устанавливается до `up --wait`, останавливает контейнеры при любом
исходе и сохраняет volumes; ошибка команды `stop` не заменяет исходный код завершения
runner. Следующий запуск поднимает тот же стенд. Тесты не используют данные приложения.

Files integration workflow в шаге `always()` выводит `docker compose logs --no-color` и
`docker compose ps --all`, а TRX-файлы загружает как artifact также при ошибке.

Для внешнего изолированного стенда можно запускать `dotnet test` напрямую, задав
`BARKCLOUD_TEST_POSTGRES`, `BARKCLOUD_TEST_RABBITMQ` и `BARKCLOUD_TEST_RABBITMQ_CONTAINER`.
`BARKCLOUD_TEST_RABBITMQ` задаётся в формате MassTransit `rabbitmq://...`: MassTransit
использует исходный URI, а подключения через RabbitMQ.Client получают URI с заменённой
на `amqp` схемой из того же адреса. Тестовый PostgreSQL-пользователь должен иметь право
`CREATE DATABASE`; Docker должен управлять указанным тестовым контейнером RabbitMQ.

## Что проверяется

- Production endpoint и consumer (`UploadProcessingQueue.ConfigureUploadProcessing`),
  persistent Quartz и настоящий PostgreSQL/RabbitMQ. Вместо S3/ffmpeg используется
  управляемый pipeline. `UploadSessionProcessor` и `UploadArtifactCleaner` настоящие;
  физическое удаление блобов подменено удалением тестовых orphan-записей PostgreSQL.
- Освобождение двух слотов между попытками, максимум две активные тяжёлые обработки.
- Рестарт Files host с сохранением расписания, просроченный таймер, redelivery count.
  Повторный запуск миграции и EF rollback/reapply сохраняют trigger и его payload.
- Остановка/запуск того же RabbitMQ с сохранённым volume во время ожидания таймера.
- Окончательная ошибка отправки при недоступном RabbitMQ: штатный транспорт может
  ждать reconnect. Тест прерывает заблокированную отправку через `IScheduler.Interrupt`,
  чтобы пройти ограниченные refire штатного `ScheduledMessageJob`. Проверяет, что
  listener сохраняет новый trigger со всем payload/headers до восстановления брокера,
  доставка переживает рестарт host и не расходует дополнительную processing-попытку.
- `Ready → NoOp` до и после рестарта, без повторного pipeline или смены `UploadedAt`.
- Cancellation при остановке host: незавершённая processing-попытка не сохраняется;
  сообщение возвращается после рестарта.
- Integrity failure сразу завершает сессию с `Failed`, без redelivery или Fault.
- Пять ошибок pipeline освобождают резерв, запускают cleanup и возвращают `Failed`.
  Пять исключений вне обработанного pipeline уходят в `_error` и Fault. В тесте
  необработанное исключение создаётся статусом `Uploading`; в рабочей системе
  ошибка БД/финализации также может оставить сессию `Processing`.

Без Docker интеграционные сценарии не выполняются. Сборка проекта подтверждает
компиляцию, но не заменяет приёмочный прогон с настоящими сервисами.

Проверки разбора RabbitMQ URI и повторного создания тестового host не требуют внешних
сервисов. `UploadTestHost` привязывает контексты логирования MassTransit и Quartz к
`ILoggerFactory` нового host до создания bus/scheduler, чтобы после остановки предыдущего
host не использовать закрытую фабрику. Эти проверки можно запустить отдельно:

```bash
dotnet test Tests/Backend/BarkCloud.Files.IntegrationTests/BarkCloud.Files.IntegrationTests.csproj \
  -c Release --filter 'FullyQualifiedName~UploadTestHostTests|FullyQualifiedName~RabbitMqClientConnectionFactoryTests'
```
