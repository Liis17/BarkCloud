# F17/F18: probe HttpClient и retry middleware

Срез `d6563a2`, .NET 10.0.203, MassTransit 8.5.2. Результат — [results.txt](results.txt), исходник — [Program.cs](Program.cs).

Из корня репозитория:

```sh
dotnet run --project Docs/audit/backend-recheck-2026-10-07/evidence/webqueue/Probe.csproj -- "$PWD"
```

Запуск занимает около 105 секунд: последние 100 секунд намеренно ждёт настоящий default timeout. Внешняя сеть и broker не нужны. DelayHandler заменяет медленный HTTP upstream; адрес `loopback.invalid` не разрешается и сетевой запрос к нему не выполняется.

Probe проверяет, что в обоих Program.cs есть текущая точная регистрация `AddHttpClient("files-upload")`, затем создаёт клиент той же регистрацией. Он печатает оба реальных Timeout, показывает непереданную отмену caller и отмену незавершающегося запроса по default timeout. Это подтверждение поведения клиента; multipart/controller/браузер целиком не исполняются.

Для F18 probe извлекает concurrency/интервалы из текущего Files/Program.cs и применяет их к MassTransit in-memory endpoint без сокращения интервалов. Два сообщения бросают контролируемые исключения, третье исправное не выполняется за 1500 мс. После наблюдения bus останавливается, 21 минуту не ждут. Это подтверждает удержание слотов middleware; RabbitMQ и UploadSessionProcessor не запускаются.

Вывод описательный; exit 0 не подтверждает отсутствие дефектов. После изменения production-конфигурации probe может потребовать адаптации. Для приёмки исправления нужны реальные long multipart upload/cancellation и поддерживаемая повторная доставка на RabbitMQ, как указано в F17/F18.
