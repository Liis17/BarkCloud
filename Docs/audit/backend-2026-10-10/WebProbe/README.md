# Локальная проверка Web для аудита

Проверка вызывает настоящие `MapWebEndpoints`, `AdminGate` и валидатор URL. HTTP upstream заменён локальным stub; gRPC и production не используются. Она слушает только `127.0.0.1:18941`. Перед запуском убедитесь, что порт свободен. Тестовые ключ и пароль в коде не являются настройками BarkCloud.

Из корня репозитория:

```powershell
dotnet run --project Docs/audit/backend-2026-10-10/WebProbe/Probe.csproj --artifacts-path "$env:TEMP/barkcloud-audit-web-evidence"
```

Добавьте `-- --serve`, чтобы открыть выведенный `BROWSER_URL` в тестовом браузере. SVG выполняет только чтение локального `/audit-marker`; успешное выполнение меняет title на `audit-script-executed` и атрибут `data-audit` на `same-origin-read-succeeded`. Завершите сервер Ctrl+C. Этот сценарий подтверждает поведение Web при SVG upstream; полный upload через Files проверен чтением исходников, а не этим stub.

Результат на аудируемом коде:

```text
SVG_PROXY status=200; type=image/svg+xml; disposition=; csp=False; script=True
ICON_HOST accepted=True; target=http://169.254.169.254/download/probe
ICON_LIMIT status=413; serializedBytes=3145728
ADMIN_UNBOUND withoutAnyUserSession=True
ADMIN_MALFORMED throws=ArgumentOutOfRangeException
FORM_BUFFER acceptedIgnoredFileBytes=8388608
```

Проверка `ICON_HOST` вызывает только валидатор через reflection; запрос к link-local адресу не выполняется. Проверка `ADMIN_UNBOUND` относится к формату cookie, не доказывает доступ без пользовательской сессии: endpoint отдельно проверяет обычную авторизацию. `FORM_BUFFER` использует реальные `FormFeature`/`FormOptions` с настройками Web, но не заполняет диск до отказа. `ICON_LIMIT` измеряет чтение 3 MiB через настоящий endpoint до ответа 413. После исправлений ожидаемый вывод должен измениться; программа печатает наблюдения, не служит тестом с автоматическим verdict.
