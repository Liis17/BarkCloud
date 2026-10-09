# Android — приложение

Parent: [[Architecture]] · Авторизация: [[Android/Authentication]] · Загрузка: [[Android/ResumableUpload]] · См. также: [[Shared/Proto]] · [[Backend/Identity]] · [[Backend/Users]] · [[Backend/Files]]

## Назначение

Нативный клиент BarkCloud на Kotlin, Jetpack Compose и Material 3. Основная навигация состоит из вкладок «Галерея», «Файлы», «Альбомы», «Корзина» и «Настройки»; по умолчанию открываются «Альбомы». В файловом разделе доступны облачные каталоги, локальный браузер, общие папки и динамические папки. Отдельные экраны обслуживают профиль, устройства, приватность, избранное, Vault и блокировку приложения.

## Источники

| Файл | Символ | Роль |
|---|---|---|
| `Android/BarkCloud.Android/app/build.gradle.kts` | `android`, `syncSharedProto` | Параметры Android и синхронизация общих proto |
| `Android/BarkCloud.Android/app/src/main/AndroidManifest.xml` | `MainActivity`, `ShareActivity`, provider, receiver | Системные точки входа |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/BarkCloudApplication.kt` | `BarkCloudApplication` | Создание общих сервисов и сетевого слоя |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/ui/navigation/RootNavGraph.kt` | `RootNavGraph` | Выбор стартового экрана, auth-граф и App Lock |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/ui/main/MainDestination.kt`, `MainScreen.kt` | `MainDestination`, `MainScreen` | Вкладки и вложенная навигация |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/cloud/CloudRepository.kt` | `CloudRepository` | Клиентские операции с файлами, каталогами, корзиной и избранным |
| `Android/BarkCloud.Android/app/src/main/java/com/barkfluff/BarkCloud/data/persistence/BarkCloudDatabase.kt` | `BarkCloudDatabase` | Локальные записи очереди и состояния медиатеки |

## Публичные контракты

- Минимальная версия — Android 11 / API 30; `compileSdk` и `targetSdk` — 36.
- Схема `barkcloud://` принимает цели `gallery`, `files`, `albums`, `media`, `trash`, `settings`, `vault`. Цели `albums` и `media` открывают вкладку «Альбомы»; `vault` ведёт к настройкам Vault.
- `ShareActivity` принимает `ACTION_SEND` и `ACTION_SEND_MULTIPLE` для MIME-групп `image/*`, `video/*`, `application/*`, `text/*`; переданные URI ставятся в общую очередь загрузки.
- Виджет хранилища показывает последний сохранённый снимок квоты и открывает `barkcloud://settings`.

## Зависимости и взаимодействия

- `BarkCloudApplication` вручную создаёт `GrpcManager`, репозитории Identity/Users/Files/Cloud/Album/DynamicFolder и службы передачи, кеша, очереди загрузок, автозагрузки и App Lock; DI-контейнер не используется.
- Proto-контракты берутся из `Shared/BarkCloud.Proto` задачей `syncSharedProto`. Серверные API описаны в заметках `Backend/*`.
- Галерея читает медиатеку через MediaStore; локальный браузер работает с внешней файловой системой и разрешением `MANAGE_EXTERNAL_STORAGE`.
- Загрузка и автозагрузка работают через WorkManager. Детали сессий и восстановления — в [[Android/ResumableUpload]]; авторизация и хранилище токенов — в [[Android/Authentication]].

## Ограничения и важные детали

- Клиент поддерживает Android API 30 и выше; системные разрешения для медиатеки и широкого доступа к файлам запрашиваются отдельно.
- Состояние очереди загрузок и присутствия медиа в облаке хранится локально в Room; оригиналы кешируются службой `FileCacheService`, превью — отдельным Coil disk cache.
- Выход через `SessionManager` отзывает сессию и очищает токены, очередь, кеш,
  локальные статусы медиатеки и Vault; автозагрузка отключается.
