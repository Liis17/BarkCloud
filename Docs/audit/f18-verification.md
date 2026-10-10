# F18 — проверка durable redelivery загрузок

## Итог

Ограниченный приёмочный сценарий F18 пройден: два долгих сбоя pipeline не удерживают оба слота process-uploaded-file до первого повтора. В проверенном сценарии здоровая сессия достигает Ready по assertion теста не позднее 5 секунд, до первой redelivery через 10 секунд. CI прошёл 12 тестов, 0 failed, 0 skipped.

Это подтверждение сценария освобождения слотов, а не гарантия глобальной durability, exactly-once, полной идемпотентности или crash safety. Настоящие PostgreSQL и RabbitMQ работают вместе с production endpoint, consumer, processor и cleaner; S3/ffmpeg pipeline и физическое удаление orphan blobs подменены. Hard-kill, fault injection PostgreSQL, конкурентная обработка одной SessionId и полный Program с bus outbox не проверялись.

Bounded code audit Sonnet 5.5 high, раунд r3, не включал чтение Quartz
`JobStoreSupport`, MassTransit `RedeliveryRetryFilter` или RabbitMQ transport internals.
В финальном раунде r1 ревьюер прочитал pinned `RedeliveryRetryFilter`,
`ScheduleMessageRedeliveryContext` и Quartz `JobRunShell`; вердикт r1 запросил
документальные правки. `JobStoreSupport` остаётся непрочитанным, а подтверждение команды
брокером не трактуется как запись Quartz job. Ниже source-traced выводы отделены от
непроведённых проверок; полный аудит Quartz и MassTransit этим не заявляется.

Историческое замечание F18 сохранено: [аудит от 2026-10-07](backend-recheck-2026-10-07/F18.md).

## Проверенная версия и артефакты

- Ветка перед CI: t3/durable-upload-redelivery.
- SHA до CI и в CI: b4c48bd6dc414f756fdb92ec1de8336365ea82a5.
- CI: [Files — durable upload redelivery, run 38050152878](https://github.com/Liis17/BarkCloud/actions/runs/38050152878). Ожидаемый SHA совпал; integration: PASS, 12 passed, 0 failed, 0 skipped.
- Команда ручного запуска production-сценария:
  `gh workflow run files-upload-integration.yml --ref master -f production-intervals=true`.
  Перед dispatch remote `master` был проверен на SHA
  `b4c48bd6dc414f756fdb92ec1de8336365ea82a5`; metadata фиксируют
  `event=workflow_dispatch`, `headBranch=master` и тот же `headSha`.
- В CI job длительностью 24 мин 01 с тест `Exhaustion_WithUnchangedProductionIntervals` занял `00:21:12.4920673` (атрибут `duration` в TRX); его stdout содержит `Complete retry chain elapsed: 00:21:10.3814954` — длительность цепочки повторов. Интервалы: 10 секунд, 1, 5 и 15 минут.
- RabbitMQ в этом CI сообщил версию 4.3.6; PostgreSQL запускался из postgres:18. Scheduler сообщил Quartz 3.15.0.0. CI использовал .NET SDK 10.0.401 и runtime 10.0.12. Это версии CI-стенда, не подтверждение версии RabbitMQ в production; digest образа не сохранён.
- CI TRX: `/tmp/barkcloud-f18-verification/ci/artifacts/files-upload-integration.trx`; run log и metadata: `/tmp/barkcloud-f18-verification/ci/run.log` и `/tmp/barkcloud-f18-verification/ci/run-metadata.json`.
- Начальный локальный build log `/tmp/barkcloud-f18-verification/01-build.log`: exit 0,
  39 warnings, 0 errors. Финальный `/tmp/barkcloud-f18-verification/final/files-build.log`:
  exit 0, 38 warnings, 0 errors; NU1902/NU1903 относятся к SixLabors.ImageSharp 3.1.12.
- Финальные локальные TRX из `/tmp/barkcloud-f18-verification/final`:
  `local-files/f18-final-files.trx` — 10 passed, 0 failed, 0 skipped;
  `local-integration/f18-final-integration.trx` — 3 passed, 0 failed, 0 skipped.
  Эти проверки прошли без Docker.
- Точные argv финального набора сохранены в `/tmp/barkcloud-f18-verification/final/argv.txt`; exit codes и сводка результатов — в `/tmp/barkcloud-f18-verification/final/verification-summary.txt`. Первоначальный набор логов не сохраняет argv; финальные argv относятся только к запуску в каталоге `final`.
- Финальный unit TRX `final/local-files/f18-final-files.trx` включает
  `ScheduledMessageRecoveryListenerTests.FinalSendFailure_PersistsPayloadAndHeadersForTransportRetry`:
  trigger через 30–31 секунду, копирование payload/header map и misfire FireNow. Этот
  unit-тест не входит в 12 CI acceptance tests.
- Resolved versions: MassTransit RabbitMQ/Quartz/EF 8.5.2; Quartz 3.15.0; RabbitMQ.Client 7.1.2; EF Core 10.0.8; Npgsql.EntityFrameworkCore.PostgreSQL 10.0.2; Npgsql 10.0.3.

Ранее сохранённые логи, metadata и TRX прочитаны без повторного запуска. В рамках этой
документальной правки сборки, тесты и CI не запускались.

## Что проверяет acceptance suite

Тесты находятся в Tests/Backend/BarkCloud.Files.IntegrationTests/UploadRedeliveryTests.cs. Используются настоящий consumer, UploadSessionProcessor, UploadArtifactCleaner, PostgreSQL и RabbitMQ. ProbePipeline подменяет S3/ffmpeg обработку, а физическая очистка orphan blobs подменена. Одновременно работает один UploadTestHost; перезапуски последовательные.

При проверке исходников подтверждены persistent PostgreSQL store Quartz и применение миграций FilesContext из Program.cs до старта hosted services. Контракт ProcessUploadedFile(Guid SessionId) не менялся.

| Тест | Проверенный результат |
|---|---|
| UploadTestHostTests.ResolvingBus_AfterPreviousHostDisposed_DoesNotUseDisposedLoggerFactory | Следующий host не использует закрытую фабрику логирования предыдущего host. |
| RabbitMqClientConnectionFactoryTests.RunnerAddress_UsesRootVirtualHost | Runner URI преобразуется с корректным root vhost. |
| RabbitMqClientConnectionFactoryTests.AddressWithEscapedCredentialsAndNamedVirtualHost_UsesDecodedValues | Raw RabbitMQ.Client получает декодированные credentials и named vhost. |
| UploadRedeliveryTests.TwoFailures_ReleaseBothSlotsBeforeFirstProductionRedelivery | Два pipeline-сбоя достигают максимума двух активных слотов; assertion требует Ready для здоровой сессии не позднее 5 секунд и до первого повтора через 10 секунд. TRX длительность 375 мс — длительность всего теста, не отдельная измеренная latency здоровой сессии. |
| UploadRedeliveryTests.Exhaustion_WithFastTestIntervals | На быстрых интервалах pipeline-сбой завершается Failed/processing_retries_exhausted, резерв освобождён, cleanup один раз. Необработанное сообщение проходит доставки 0–4 и попадает в _error с Fault; fixture начинается в Uploading и не проверяет конечный status. |
| UploadRedeliveryTests.Exhaustion_WithUnchangedProductionIntervals | Те же исходы проверены с production-интервалами. Pipeline-сессия имеет ProcessingAttempts=5, резерв 0 и один cleanup. Необработанное сообщение проходит доставки 0–4, попадает в _error и публикует Fault; fixture начинается в Uploading и не проверяет конечный status. |
| UploadRedeliveryTests.IntegrityFailure_ReturnedFailed_DoesNotRedeliver | Integrity error даёт Failed/integrity_mismatch без redelivery и Fault; резерв освобождён, cleanup один раз. |
| UploadRedeliveryTests.FilesRestart_PreservesRedeliveryAndFiresOverdueTimer | После остановки Files тест ждёт 12 секунд при первоначальном интервале 10 секунд, затем сохранённая доставка срабатывает после рестарта; rollback/reapply EF migration сохраняет pending delivery и redelivery count. Просрочка около 2 секунд не доказывает поведение при misfire старше порога 60 секунд. |
| UploadRedeliveryTests.RabbitRestartDuringWait_PreservesMessageAndRedeliveryCount | Stop/start того же RabbitMQ container во время ожидания сохраняет доставку; после восстановления зафиксированы delivery 0, 1 и две processing-попытки. Force-recreate контейнера этот тест не выполняет. |
| UploadRedeliveryTests.TerminalSendFailureWhileRabbitDown_PersistsRecoveryAcrossFilesRestart | При остановленном RabbitMQ тест прерывает зависший Quartz job через IScheduler.Interrupt, проверяет durable recovery trigger и полный JobDataMap, затем последовательно перезапускает Files. Это не hard-kill и не отказ PostgreSQL при сохранении trigger. |
| UploadRedeliveryTests.ReadyReplay_BeforeAndAfterRestart_DoesNotRepeatPipelineOrSuccess | Последовательный replay после Ready до и после рестарта не повторяет pipeline: один вызов, одна processing-попытка, прежний UploadedAt. По коду Ready, Failed, Cancelled и Expired дают NoOp; тест проверяет Ready. |
| UploadRedeliveryTests.CancellationDuringShutdown_RequeuesWithoutSpendingProcessingAttempt | Graceful остановка отменяет выполняющийся pipeline; после рестарта сообщение доставляется повторно, первая отмена не расходует ProcessingAttempts. |

Тестовый host не регистрирует EF bus outbox. `UploadTestHost.SendAsync(Guid)` получает endpoint через `Bus.GetSendEndpoint(...)`, затем вызывает `endpoint.Send(new ProcessUploadedFile(id), timeout.Token)`; это исключает CompleteUploadSession и полную композицию Program.cs из покрытия. Для интеграции Complete/outbox нужна отдельная проверка restart и at-least-once delivery с проверяемым идемпотентным итогом; exactly-once поведение этим не обещается.

## Ответы на замечания независимого ревью

- Отказ PostgreSQL при записи scheduler command не воспроизводился. Source-traced путь
  допускает попадание команды в `files-upload-scheduler_error` и остановку автоматического
  продвижения этой доставки, но не доказывает потерю payload. Broker confirmation
  `ScheduleMessage` не означает, что scheduler consumer записал Quartz job.
  `1.25` секунды — только сумма пяти задержек endpoint retry `5 × 250 мс`, без времени
  операций PostgreSQL; это не порог outage и не основание обобщать на все состояния
  `Processing`.
- Сужено утверждение о рассинхронизации счётчиков: delivery count и ProcessingAttempts независимы, но любое отставание не означает дефект. Обычная стабильная ошибка pipeline достигает processor Failed-ветви при сохранённой пятой попытке; успешная обработка может дать Ready раньше. Риск terminal lifecycle проявляется, если на последней delivery ProcessingAttempts после инкремента остаётся меньше 5 либо падает сохранение terminal state.
- Отклонено утверждение о безусловном hard-kill при shutdown. Принудительное завершение возможно, если pipeline не закончит работу за эффективный grace period; тесты подтверждают только graceful cancellation/restart и сами по себе не воспроизводят hard-kill.

## Source-traced findings и локальные verification tasks

**A — HIGH, остановка автоматического прогресса при длительном отказе PostgreSQL scheduler.**

**Место.** `UploadProcessingQueue.ConfigureUploadProcessing` и package versions:
`Backend/BarkCloud.Files/Scheduling/UploadProcessingQueue.cs`,
`Backend/BarkCloud.Files/BarkCloud.Files.csproj:27–31`. Scheduler endpoint:
`ScheduleMessageConsumerDefinition`, `ScheduleMessageConsumer.Consume` и
`ScheduleMessageConsumer.EnsureJobExists`.

**Условие.** В MassTransit.Quartz 8.5.2 endpoint применяет
`UseMessageRetry(Interval(5, 250))`, а `ScheduleMessageConsumer.Consume` записывает trigger
через `IScheduler.ScheduleJob`. Если PostgreSQL продолжает отклонять запись до исчерпания
retry, команда может попасть в `files-upload-scheduler_error`; автоматическое продвижение
этой доставки остановится. `RedeliveryRetryFilter.Send` ожидает
`ScheduleRedelivery` до `NotifyConsumed`; `ScheduleMessageRedeliveryContext.ScheduleRedelivery`
возвращает задачу `ScheduleSend`. Поэтому исходная обработка удерживает один
`process-uploaded-file` consumer slot до завершения отправки scheduler command, а исходная
delivery не доходит до `NotifyConsumed`/ACK прежде этого завершения.
Publisher confirms настроены в [инфраструктуре проекта](../../Obsidian/BarkCloudVault/Platform/Infrastructure.md):
они относятся к приёму команды брокером, не к сохранению Quartz job в PostgreSQL.
Сохранение команды в error queue останавливает автоматический progress, но не доказывает
потерю payload. `1.25` секунды — сумма задержек retry `5 × 250 мс` без времени операций
БД; это не outage threshold. RabbitMQ transport internals не читались.

**Доказательство.** Вывод source-traced, не воспроизведён fault-injection тестом.
Acceptance CI не проверяет отказ PostgreSQL при записи scheduler command.

Источники: [`ScheduleMessageConsumerDefinition.cs` 8.5.2](https://github.com/MassTransit/MassTransit/blob/v8.5.2/src/Scheduling/MassTransit.QuartzIntegration/Configuration/Configuration/ScheduleMessageConsumerDefinition.cs),
[`ScheduleMessageConsumer.cs` 8.5.2](https://github.com/MassTransit/MassTransit/blob/v8.5.2/src/Scheduling/MassTransit.QuartzIntegration/QuartzIntegration/ScheduleMessageConsumer.cs),
[`RedeliveryRetryFilter.cs` 8.5.2](https://github.com/MassTransit/MassTransit/blob/v8.5.2/src/MassTransit/Middleware/RedeliveryRetryFilter.cs)
и
[`ScheduleMessageRedeliveryContext.cs` 8.5.2](https://github.com/MassTransit/MassTransit/blob/v8.5.2/src/MassTransit/Contexts/Context/ScheduleMessageRedeliveryContext.cs).

**Задача + Acceptance.** Внедрить детерминированный PostgreSQL fault при записи schedule;
дождаться фактического исчерпания retry, не используя фиксированное 5-секундное ожидание;
восстановить PostgreSQL; зафиксировать depth `files-upload-scheduler_error`, Quartz
triggers, upload queue, status/reserve и возможность операторского replay сохранённого
payload. Выбор durable recovery/operator policy — отдельное решение; не удалять сообщение
и не заменять этот сценарий долгими upload retry.

**B — MEDIUM, terminal lifecycle может не совпасть с исчерпанием доставок.**

Место: Backend/BarkCloud.Files/Services/UploadSessionProcessor.cs:47–123, 125–176; Backend/BarkCloud.Files/Services/UploadSessionMaintenance.cs:21–24, 65–94; Backend/BarkCloud.Files/Consumers/ProcessUploadedFileConsumer.cs:13–30. ProcessingAttempts увеличивается перед pipeline, StorageMigrationPausedException уменьшает его и повторно выбрасывается, а redelivery count расходуется отдельно. Сохранения generic catch, FailAsync или финального Ready могут сами завершиться ошибкой. Исключение до pipeline также может выйти за processing ветвь. Если на пятой delivery значение ProcessingAttempts после инкремента всё ещё меньше 5, processor Failed-ветвь не запускается; сообщение может уйти в _error/Fault при сохранённом Processing и резерве. Maintenance истекает multipart Uploading, а Processing восстанавливает только для legacy с пустым MultipartUploadId. Это условный gap, не утверждение о любом отставании счётчика.

Локальная проверка и acceptance: fixture начинает с multipart Processing; один раз срывает сохранение или вводит pause, затем оставляет ошибки pipeline до исчерпания redelivery. Проверить _error и Fault, persisted status, ReservedBytes, CleanupPending и фактический вызов cleanup. Отдельно согласовать reconciliation policy; fixture, начинающаяся в Uploading, не подтверждает этот lifecycle.

**C — MEDIUM, не проверены production shutdown и crash windows.**

Место: Tests/Backend/BarkCloud.Files.IntegrationTests/UploadTestHost.cs:103–109, 158–162; Backend/BarkCloud.Files/Program.cs:151–180; Backend/docker-compose.yml:73–90. Тестовый host задаёт ConsumerStopTimeout=1s и StopTimeout=15s. Production Program эти MassTransit timeout явно не задаёт, а compose Files не задаёт stop_grace_period. Если pipeline не завершается в действующий container grace period, процесс может быть принудительно завершён; текущий тест этого не воспроизводит. Нельзя утверждать, что production всегда получает SIGKILL или что cancellation невозможна.

Локальная проверка и acceptance: взять production shutdown configuration и проверить kill points до первой durable записи попытки, во время pipeline, после durable Ready до ACK и в Quartz после publish до завершения job. После каждого рестарта записать attempts, deliveries, status, reserve и side-effect counts. Учесть, что общий FilesContext может сохранить ProcessingAttempts через pipeline SaveChanges; crash до такого сохранения может оставить счётчик прежним. Повторные crash до checkpoint могут повторяться без роста delivery count — это гипотеза для проверки, не доказанный poison-message сценарий.

**D — MEDIUM, конкурентная обработка одной SessionId не проверена.**

Место: Backend/BarkCloud.Files/Services/UploadSessionProcessor.cs:47–67; Backend/BarkCloud.Files/Domain/UploadSession.cs:62–66; Backend/BarkCloud.Files/Persistence/FilesContext.cs:97–103. Два слота могут независимо прочитать одну Processing-сессию; ConcurrencyToken защищает конкурентную запись EF, но не изолирует внешние pipeline side effects до конфликта записи. Дублирующая доставка не означает, что side effects обязательно повторятся.

Локальная проверка и acceptance: синхронно подать одну SessionId в два слота и сверить число pipeline side effects, Ready/UploadedAt, attempts, cleanup, Fault и резерв. Не предполагать single-pipeline claim без отдельного implementation решения.

**E — MEDIUM, persistence failure recovery listener не покрыт отказом PostgreSQL.**

**Место.** `ScheduledMessageRecoveryListener.JobWasExecuted` в
`Backend/BarkCloud.Files/Scheduling/ScheduledMessageRecoveryListener.cs:14–34`;
`ScheduledMessageRecoveryListenerTests` в
`Tests/Backend/BarkCloud.Files.Tests/Scheduling/ScheduledMessageRecoveryListenerTests.cs:13–42`
использует mock `IScheduler`. `ScheduleMessageConsumer.EnsureJobExists` создаёт durable
`ScheduledMessageJob` с `RequestRecovery().StoreDurably()`:
[`ScheduleMessageConsumer.cs` 8.5.2](https://github.com/MassTransit/MassTransit/blob/v8.5.2/src/Scheduling/MassTransit.QuartzIntegration/QuartzIntegration/ScheduleMessageConsumer.cs).

**Условие.** В Quartz 3.15.0 `JobRunShell.NotifyJobListenersComplete` возвращает `false`
при `SchedulerException`; `JobRunShell.Run` после этого делает `break` до
`NotifyJobStoreJobComplete`. Если ошибка записи recovery trigger из listener доходит до
уведомления listeners как `SchedulerException`, job-store completion path пропускается.
Это source inference о control flow: он не устанавливает, сохраняется ли fired row,
удаляется ли он или будет восстановлен. `JobStoreSupport` не читался; нельзя обещать ни
recovery, ни отсутствие потери.

Источник Quartz:
[`JobRunShell.cs` v3.15.0](https://github.com/quartznet/quartznet/blob/v3.15.0/src/Quartz/Core/JobRunShell.cs).

**Доказательство.** PostgreSQL outage при сохранении recovery trigger не воспроизводился
в listener unit test или acceptance CI; unit test использует mock `IScheduler`.

**Задача + Acceptance.** Вызвать PostgreSQL fault во время listener `ScheduleJob`;
проверить fired triggers и payload/headers до рестарта, после restart и после Quartz
recovery/clustering. Не делать вывода о guaranteed no-loss до результата.

**F — MEDIUM, identity RabbitMQ при force-recreate — условный риск, не воспроизведённый дефект.**

Место: Tests/Backend/BarkCloud.Files.IntegrationTests/docker-compose.yml:16–30; Tests/Backend/BarkCloud.Files.IntegrationTests/UploadRedeliveryTests.cs:100–115, 327–340. Тест использует rabbitmq:latest и постоянный volume; в compose не заданы hostname или RABBITMQ_NODENAME. Покрыт stop/start того же container, не force-recreate. RabbitMQ формирует node name из hostname и по умолчанию использует hostname в имени каталога БД: [RabbitMQ Clustering Guide](https://www.rabbitmq.com/docs/clustering). При изменении hostname с существующим volume поведение может отличаться; текущий CI этого риска не проверил.

Локальная проверка и acceptance: force-recreate RabbitMQ с pending messages и scheduler triggers, проверить очереди, Quartz delivery и payload. Перед предложением стабильного identity оценить миграцию существующих данных; не менять hostname вслепую.

**G — MEDIUM, transport retry после ошибки job не имеет установленного конечного лимита.**

Место: Backend/BarkCloud.Files/Scheduling/ScheduledMessageRecoveryListener.cs:17–31; pinned MassTransit 8.5.2 `ScheduledMessageJob.Execute`. Listener создаёт новый trigger через 30 секунд после окончательной ошибки send; transport retry отделён от конечных пяти business deliveries. Для постоянно неверного destination повторы trigger могут продолжаться; это гипотеза по source, не воспроизведённая ошибка. Источник: [ScheduledMessageJob.Execute 8.5.2](https://github.com/MassTransit/MassTransit/blob/v8.5.2/src/Scheduling/MassTransit.QuartzIntegration/QuartzIntegration/ScheduledMessageJob.cs).

Локальная проверка и acceptance: подать ошибочный destination, классифицировать постоянную ошибку и временный outage, проверить сохранность payload/headers и операторский replay. Не вводить произвольный cap, который удаляет payload.

## Остальные границы доказательства

- F18 IntegrationTests не проверяет полный Program/EF bus outbox путь; будущий Complete/outbox restart test должен проверять at-least-once и идемпотентный итог, без exactly-once обещания.
- `FilesRestart_PreservesRedeliveryAndFiresOverdueTimer` ждёт 12 секунд при первоначальном интервале 10 секунд, поэтому Quartz trigger просрочен примерно на 2 секунды к запуску нового host. Это не проверка misfire после просрочки более 60 секунд; нужен отдельный long-overdue сценарий.
- Код объявляет durable scheduler endpoint и MassTransit/RabbitMQ настройки, однако фактические production queue properties и publisher confirm state в живом брокере не инспектировались. Версия RabbitMQ 4.3.6 относится к CI; production version и image digest не установлены.
- consumer_timeout для production rabbitmq:latest не объявляется дефектом: фактическая production конфигурация брокера неизвестна.
