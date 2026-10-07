# Доказательства повторной проверки backend

Срез `d6563a2160ca8e3b870c32871883aa6fa188b923`, 2026-10-07. Здесь диагностические проекты и сохранённые результаты для заданий из [report.md](../../../../report.md). Они не добавлены в solution/CI и не входят в 949 штатных тестов этой ревизии.

- [Files](files/README.md): реальные Attach/Rename/Search/Purge и PostgreSQL 18; проверки корректного поведения **падают** на F13/F15/F20. S3 в F15 заменён mock.
- [Identity](identity/README.md): реальные SetPassword/outbox и PostgreSQL; настоящий SessionIssuer с управляемым gRPC; helper/signer/validator JWT для границ ключа. F02 подтверждён отдельно штатным PostgreSQL regression.
- [Torrent](torrent/README.md): реальные API/store/context/flush и SQLite; Pause/Start MonoTorrent заменены protected hooks. Probe **успешен при подтверждении дефектов**, его exit 0 не означает исправность.
- [Web/Queue](webqueue/README.md): HttpClient и MassTransit 8.5.2 in-memory, настройки считаны из текущих Program.cs. Полный HTTP upload и RabbitMQ в этом probe не запускаются.

В проектах с PostgreSQL требуется новый `BARKCLOUD_TEST_POSTGRES` на отдельный тестовый сервер; временные кластеры автора уже остановлены. Подключение из окружения не сохраняется в документах. Проекты используют production-код текущего checkout: после исправления старые результаты служат историей, а assertions/барьеры нужно адаптировать в штатные регрессии.
