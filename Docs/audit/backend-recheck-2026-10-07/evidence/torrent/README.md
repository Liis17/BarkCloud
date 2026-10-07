# Повторяемые проверки F21/F22

Срез `d6563a2`, проверка 2026-10-07. Исходники рядом: `TorrentResidualProbe.csproj`, `Program.cs`; сохранённый вывод — `results.txt`.

Из корня репозитория:

```sh
dotnet run --project Docs/audit/backend-recheck-2026-10-07/evidence/torrent/TorrentResidualProbe.csproj -c Release --no-launch-profile
```

Нужен .NET 10 и восстановление NuGet-пакетов. Внешние БД, RabbitMQ и production-конфигурация не нужны. Проект ссылается на актуальные production Torrent и TestKit через относительные `ProjectReference`; он не подключён в solution/CI. Проверка создаёт отдельный временный SQLite-файл, fake magnet без старта peer-обмена и каталог cache; после завершения удаляет свой временный каталог.

Проверка намеренно подтверждает наличие дефектов на указанном срезе: она завершается `0`, когда воспроизведены F21 и оба сценария F22, и бросает исключение, если старое дефектное поведение изменилось. После исправления production-кода эти assertions нужно инвертировать/перенести в штатные regression-тесты; exit `0` этого probe не означает отсутствие ошибок.

F21 исполняет настоящий приватный `FlushAsync` через reflection, настоящий монитор MonoTorrent и relational save. Reflection `SpeedMonitor.AddDelta` задаёт входной трафик; `SaveChangesInterceptor` имитирует отказ до commit.

F22 исполняет настоящие API/Store/Context и настоящий engine gate. Protected Pause/Start заменены успешными hooks, записывающими последний режим; настоящий MonoTorrent-менеджер зарегистрирован, но peer-сеть не стартует. Отдельные EF-контексты и `SaveChangesInterceptor` воспроизводят порядок конкурентных RPC и отказ БД. SQLite используется для relational change tracking; эти проверки не являются повторным PostgreSQL или полноценным peer-engine end-to-end прогоном.

Штатные тесты были запущены отдельно:

```sh
dotnet test Tests/Backend/BarkCloud.Torrent.Tests/BarkCloud.Torrent.Tests.csproj -c Release --no-restore --logger 'console;verbosity=normal'
```

Результат: `26/26 passed`, `0 skipped`. Компилятор выдавал существующие nullable warnings в зависимостях; тестовый runner также выводил уведомление о лицензии FluentAssertions. Ошибок сборки/тестов не было.
