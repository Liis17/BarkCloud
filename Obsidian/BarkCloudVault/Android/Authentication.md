# Android — авторизация и выбор сервера

Parent: [[Android/AndroidApp]] · Серверные контракты: [[Backend/Identity]] · [[Backend/Users]] · Общие proto: [[Shared/Proto]] · Метаданные клиентов: [[Shared/SharedLibraries]]

## Назначение

Compose-поток первого запуска, выбора сервера, входа, регистрации и сброса пароля. Навигацию и границу между обычной сессией и незавершённой регистрацией задаёт `RootNavGraph`; формы используют отдельные ViewModel и репозитории.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/ui/navigation/RootNavGraph.kt` | `RootNavGraph` | Выбор маршрута по серверу, сессии, onboarding и pending-регистрации |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/ui/server/ServerViewModel.kt` | `ServerViewModel` | Проверка настроек и доступности сервисов |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/grpc/ServerSettings.kt` | `ServerConfig`, `ServerSettings`, `validateServerConfig` | Валидация и сохранение сервера |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/grpc/ServerConnectionProbe.kt` | `ReflectionServerConnectionProbe` | Проверка gRPC reflection на портах |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/AuthRepository.kt` | `AuthRepository` | Вход, получение токенов и выход |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/AuthFlowRepository.kt` | `AuthFlowRepository` | Регистрация, подтверждение и сброс пароля |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/TokenStore.kt`, `TokenRefresher.kt` | `TokenStore`, `TokenRefresher` | Шифрование и обновление сессии |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/PendingRegistrationStore.kt` | `PendingRegistrationStore` | Незавершённая регистрация отдельно от активной сессии |

## Публичные контракты

- Сервер задаётся хостом и отдельными портами Identity, Users и Files; начальные значения приходят из `BuildConfig`. Хост допускает схему HTTP/HTTPS, но не userinfo, порт или путь; каждый порт должен быть в диапазоне 1–65535.
- Перед сохранением настроек приложение параллельно проверяет gRPC reflection и ожидаемые сервисы Identity, Users и Files. Настройки принимаются только после успеха всех трёх проверок; наличие reflection не подтверждает доступность БД или файлового хранилища.
- Новый сервер по умолчанию использует штатную проверку TLS. Исключение цепочки сертификатов можно включить отдельно для выбранного хоста; при изменении хоста флаг сбрасывается.
- Вход принимает username либо email и пароль; если сервер требует OTP, форма переключается на ввод кода и допускает повторный запрос после cooldown. Подробная семантика OTP остаётся в [[Backend/Identity]].
- Регистрация при включённом подтверждении почты проходит через код подтверждения и установку пароля; если сервер сразу выдаёт registration refresh-token, шаг подтверждения пропускается.
- Новый пароль проверяется клиентом: минимум 8 Unicode code points и максимум 72 байта UTF-8. Код подтверждения фильтруется до шести ASCII-цифр.
- Сброс пароля завершается отправкой кода и нового пароля одной операцией; запрос на отзыв других сессий включён по умолчанию.

## Зависимости и взаимодействия

- `AuthRepository` вызывает Identity для входа и выхода; `AuthFlowRepository` обращается к Identity для регистрации/сброса и к Users для проверки доступности username. Ошибки gRPC отображаются по error code и trailer `x-retry-after-seconds`.
- `ClientMetadataInterceptor` добавляет device/app metadata к gRPC-вызовам; `AuthInterceptor` добавляет актуальный `x-auth-token`, кроме методов `Auth` и `CreateToken`.
- `TokenStore` хранит один AES-GCM-шифротекст токенов в credential-protected SharedPreferences; ключ AES-256 создаётся в Android Keystore. При первом чтении переносит старые значения из `EncryptedSharedPreferences`.
- `TokenRefresher` сериализует параллельное обновление access-token и обновляет его за 60 секунд до истечения. Недействительный refresh очищает сессию.
- `PendingRegistrationStore` хранит отдельную AES-GCM-запись с ключом сервера, профилем, registration refresh-token и сроком действия. Пароль туда не записывается, а pending-токен не включает основную сессию.
- После успешной установки пароля токены сохраняются в обычную сессию, затем pending-запись удаляется. При повторном продолжении сначала проверяется, не был ли пароль уже принят сервером. Серверные контракты описаны в [[Backend/Identity]].

## Ограничения и важные детали

- Смена сервера отбрасывает pending-регистрацию, привязанную к предыдущей конфигурации.
- Пароль и коды подтверждения не попадают в `SavedStateHandle`; для восстановления регистрации сохраняются только несекретные поля профиля. В сбросе через `SavedStateHandle` сохраняется логин, а reset ID и секретные поля остаются состоянием текущего ViewModel.
- `AppLock` отображается поверх основного графа только при активной сессии; выход из сессии уводит из main на login.
