# Подключение Cloudflare R2 к микросервису Files

Дата исследования: 2026-09-05

## Краткий вывод

Да, Cloudflare R2 можно использовать как S3-хранилище для BarkCloud.Files. Архитектурно сервис уже построен на AWS S3 SDK и не использует MinIO API напрямую. R2 поддерживает нужные операции: GetObject, PutObject, DeleteObject, GetBucketLocation и byte range-запросы.

Однако текущий код не готов к надёжному переключению на R2 одним изменением endpoint. Перед production-переключением нужно:

1. отключить streaming SigV4 и автоматическую проверку checksum в PutObjectRequest — это прямо требуется для AWS SDK .NET при работе с R2;
2. заранее создать R2-бакеты и выдать Files ограниченные credentials;
3. перенастроить два раздела S3Buckets в Configuration и перезапустить files;
4. отдельно решить, что показывать в физической статистике хранилища: текущая реализация считает локальный диск MinIO, а не объём R2;
5. для существующей инсталляции сначала перенести объекты из MinIO в R2 с сохранением ключей.

Итоговая оценка: **совместимо после небольшой правки S3-адаптера и настройки инфраструктуры; полная миграция требует отдельного runbook**.

## Что сейчас делает Files

- Проект использует AWSSDK.S3 4.0.23.5 и AWSSDK.Core 4.0.7.4: [BarkCloud.Files.csproj](../../Backend/BarkCloud.Files/BarkCloud.Files.csproj#L10-L12).
- S3BucketRegistry читает секцию S3Buckets, создаёт AmazonS3Client с BasicAWSCredentials, ServiceURL и ForcePathStyle, а затем сопоставляет тип файла с реальным именем бакета: [S3BucketRegistry.cs](../../Backend/BarkCloud.Files/Infrastructure/S3BucketRegistry.cs#L62-L105).
- В проекте два логических бакета: user-avatars и cloud-files. Аватары и облачные файлы выбираются через одну и ту же S3-обёртку: [S3BucketRegistry.cs](../../Backend/BarkCloud.Files/Infrastructure/S3BucketRegistry.cs#L18-L45).
- S3Uploader использует только стандартные операции S3: PutObjectAsync, GetObjectAsync, DeleteObjectAsync и GetObjectAsync с ByteRange: [S3Uploader.cs](../../Backend/BarkCloud.Files/Infrastructure/S3Uploader.cs#L14-L86).
- Клиенты сейчас не получают S3-ссылку. Они загружают файл в HTTP endpoint Files, а сервис сам проксирует поток в S3; скачивание также идёт через FilesController: [FilesController.cs](../../Backend/BarkCloud.Files/Host/FilesController.cs#L25-L107), [FileUrlHelper.cs](../../Backend/BarkCloud.Files/Helpers/FileUrlHelper.cs#L25-L32). Поэтому для текущего сценария не нужны публичный R2 bucket или CORS.
- S3-клиенты создаются как singleton через AddMinioS3: [ServiceCollectionExtensions.cs](../../Backend/BarkCloud.Files/Extensions/ServiceCollectionExtensions.cs#L5-L18), а бакеты проверяются при запуске приложения: [Program.cs](../../Backend/BarkCloud.Files/Program.cs#L134-L144).

## Сопоставление с R2

| Проверка | Состояние |
|---|---|
| S3 SDK | Подходит: Cloudflare заявляет совместимость R2 с S3 SDK и рекомендует endpoint https://ACCOUNT_ID.r2.cloudflarestorage.com. См. [R2 S3](https://developers.cloudflare.com/r2/get-started/s3/) и [пример AWS SDK для .NET](https://developers.cloudflare.com/r2/examples/aws/aws-sdk-net/). |
| Credentials | Подходят Access Key ID и Secret Access Key из R2 API Token. Для Files достаточно Object Read & Write, желательно ограничить token двумя нужными бакетами. См. [R2 Authentication](https://developers.cloudflare.com/r2/api/tokens/). |
| Регион | Для R2 используется auto; Cloudflare также указывает, что пустой регион и us-east-1 являются alias этого значения. См. [S3 API compatibility](https://developers.cloudflare.com/r2/api/s3/api/#bucket-region). |
| Upload | PutObject поддерживается, но для AWS SDK .NET Cloudflare требует DisablePayloadSigning = true и DisableDefaultChecksumValidation = true, потому что R2 не поддерживает используемый SDK streaming SigV4. См. [AWS SDK for .NET](https://developers.cloudflare.com/r2/examples/aws/aws-sdk-net/#upload-and-retrieve-objects). |
| Download | GetObject и Range поддерживаются. Это покрывает обычную загрузку, просмотр и текущий HTTP streaming: [таблица совместимости R2](https://developers.cloudflare.com/r2/api/s3/api/#implemented-object-level-operations). |
| Delete | DeleteObject поддерживается: [таблица совместимости R2](https://developers.cloudflare.com/r2/api/s3/api/#implemented-object-level-operations). |
| Проверка бакета | GetBucketLocation и CreateBucket отмечены как поддерживаемые R2: [bucket-level operations](https://developers.cloudflare.com/r2/api/s3/api/#implemented-bucket-level-operations). Но права на создание бакета лучше не давать рабочему token; бакеты следует создать заранее. |
| Metadata | Content-Type поддерживается; текущая пользовательская metadata original-filename ASCII-совместима и не конфликтует с R2: [PutObject compatibility](https://developers.cloudflare.com/r2/api/s3/api/#implemented-object-level-operations), [R2 extensions](https://developers.cloudflare.com/r2/api/s3/extensions/). |

## Обязательная правка перед R2

Сейчас S3Uploader.UploadAsync создаёт PutObjectRequest без двух R2-совместимых флагов. Для первой реализации нужно добавить их в этот общий адаптер — тогда поправка автоматически покроет обычные файлы, аватары, JPEG views и все превью:

~~~csharp
var request = new PutObjectRequest
{
    BucketName = bucket,
    Key = key,
    InputStream = data,
    AutoCloseStream = false,
    AutoResetStreamPosition = false,
    ContentType = contentType,
    DisablePayloadSigning = true,
    DisableDefaultChecksumValidation = true,
    Metadata = { ["original-filename"] = Path.GetFileName(key) }
};
~~~

Это снижает встроенные проверки целостности SDK до HTTPS, поэтому endpoint должен быть только HTTPS. Cloudflare предупреждает о таком trade-off в примере .NET; прикладной SHA-256 файла в Files при этом продолжает считаться отдельно.

AuthenticationRegion = "auto" можно задать явно в AmazonS3Config для читаемости и стабильности конфигурации. AWS SDK описывает это свойство как регион для AWS4-подписи: [ClientConfig.AuthenticationRegion](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/Runtime/TClientConfig.html). Это не обязательная правка: официальный .NET-пример Cloudflare задаёт только ServiceURL, а документация R2 допускает alias us-east-1.

Текущий ForcePathStyle не является блокером для R2. BucketS3Options.ForcePathStyle фактически всегда возвращает true, несмотря на одноимённый ключ в seed-конфигурации: [BucketS3Options.cs](../../Backend/BarkCloud.Files/Configurations/BucketS3Options.cs#L24-L32). Для первого подключения это можно оставить: официальный .NET-пример R2 показывает account endpoint с path-style URL. Если позже нужно поддерживать разные S3-провайдеры с разными правилами addressing, свойство стоит сделать обычным bool с default true.

## Конфигурация R2

R2 endpoint один и тот же для обоих бакетов; имя бакета передаётся отдельно. Рекомендуемая схема:

| Configuration ServiceId | Section | Key | Значение |
|---|---|---|---|
| Files | S3Buckets:user-avatars | ServiceUrl | https://ACCOUNT_ID.r2.cloudflarestorage.com |
| Files | S3Buckets:user-avatars | AccessKey | R2 Access Key ID |
| Files | S3Buckets:user-avatars | SecretKey | R2 Secret Access Key |
| Files | S3Buckets:user-avatars | BucketName | заранее созданный R2 bucket для аватаров |
| Files | S3Buckets:cloud-files | ServiceUrl | https://ACCOUNT_ID.r2.cloudflarestorage.com |
| Files | S3Buckets:cloud-files | AccessKey | тот же или отдельный scoped Access Key ID |
| Files | S3Buckets:cloud-files | SecretKey | тот же или отдельный Secret Access Key |
| Files | S3Buckets:cloud-files | BucketName | заранее созданный R2 bucket для файлов |

Эти записи уже предусмотрены seed-ом: [ConfigurationSeed.cs](../../Backend/BarkCloud.Configuration/Infrastructure/ConfigurationSeed.cs#L86-L97). Configuration подставляет MinIO-дефолты только в пустые значения; непустые ручные значения не перезаписываются: [ConfigurationDefaultsPopulator.cs](../../Backend/BarkCloud.Configuration/Infrastructure/ConfigurationDefaultsPopulator.cs#L192-L237). Каждый сервис забирает свою конфигурацию при старте: [WebApplicationBuilderExtensions.cs](../../Backend/BarkCloud.GrpcServer/WebApplicationBuilderExtensions.cs#L72-L112). Поэтому после изменения нужно перезапустить files: его S3BucketRegistry кэшируется.

ForcePathStyle можно не менять: текущая модель его игнорирует и всегда включает path-style. Чтобы изменить это поведение, потребуется отдельная правка модели и registry.

### Бакеты и права

Cloudflare в quickstart рекомендует создать бакет через Dashboard/Wrangler, а для S3 API token — выдать Object Read & Write и при необходимости ограничить его конкретными бакетами: [R2 S3 setup](https://developers.cloudflare.com/r2/get-started/s3/).

Практический вывод (это вывод из сопоставления двух схем прав): не рассчитывать на автоматическое создание бакетов рабочим token. Отдельный Cloudflare API для создания бакета требует permission Workers R2 Storage Write: [Create Bucket API](https://developers.cloudflare.com/api/resources/r2/subresources/buckets/methods/create/). При этом Files на старте вызывает GetBucketLocation, а при 404 — PutBucket: [S3BucketInitializer.cs](../../Backend/BarkCloud.Files/Infrastructure/S3BucketInitializer.cs#L44-L78). Если bucket не создан или имя ошибочно, этот fallback может привести к AccessDenied и остановить сервис.

Для production лучше:

- создать оба bucket заранее;
- выдать token только на чтение/запись объектов этих bucket;
- оставить S3BucketInitializer как проверку существования;
- не включать публичный доступ к bucket.

## Что не переключится автоматически

### 1. Физическая статистика хранилища

PhysicalStorageStatsProvider по умолчанию сканирует /mnt/minio-data, считает размер каталога и свободное место локального диска: [PhysicalStorageStatsProvider.cs](../../Backend/BarkCloud.Files/Services/PhysicalStorageStatsProvider.cs#L5-L15), [PhysicalStorageStatsProvider.cs](../../Backend/BarkCloud.Files/Services/PhysicalStorageStatsProvider.cs#L87-L126). При R2 этот путь не содержит объектов, поэтому s3_used_storage будет 0, а total_available_storage и disk_used_storage будут описывать диск контейнера, а не R2.

Логическая пользовательская квота продолжит считаться по данным Files DB (UploadFile.Size), поэтому сама проверка объёма пользователя не обязана сломаться. Но UI/мобильные клиенты, которые показывают физические поля total_available_storage, disk_used_storage, s3_used_storage, будут показывать неверную картину: [GetUserStorageInfoCommandHandler.cs](../../Backend/BarkCloud.Files/Features/GetUserStorageInfo/GetUserStorageInfoCommandHandler.cs#L45-L64).

Нужна отдельная продуктовая договорённость: либо убрать/переименовать физические показатели для R2, либо получать usage/лимиты через Cloudflare API, либо показывать только логическое использование Files DB.

### 2. Compose и BarkCloud.Builder

Production compose и генератор Builder жёстко описывают MinIO: сервис minio, MINIO_* environment, volume /mnt/minio-data: [docker-compose.yml](../../Backend/docker-compose.yml#L67-L79), [docker-compose.yml](../../Backend/docker-compose.yml#L164-L178), [BackendComposeGenerator.cs](../../Tools/BarkCloud.Builder/BackendComposeGenerator.cs#L71-L125), [BackendComposeGenerator.cs](../../Tools/BarkCloud.Builder/BackendComposeGenerator.cs#L257-L274).

Это не мешает временно использовать R2 при ручной записи настроек в Configuration DB: MinIO станет неиспользуемым. Но для чистой R2-инсталляции стоит позже добавить storage backend в Builder/compose, убрать обязательный MinIO volume и не передавать StorageProbe как MinIO-статистику. Сейчас Builder не умеет сгенерировать R2 endpoint/credentials.

### 3. Размер больших объектов

Текущий S3Uploader делает один PutObject, а не явный multipart pipeline. Cloudflare указывает лимит single upload 5 GiB и multipart до 5 TiB: [Upload objects](https://developers.cloudflare.com/r2/objects/upload-objects/). Если Files должен принимать объекты больше 5 GiB, нужно добавить multipart upload либо явно ограничить размер до переключения на R2. Это отдельное ограничение текущего upload path, а не препятствие для обычных файлов.

## Миграция с существующего MinIO

Простая смена endpoint не переносит данные. В БД Files хранятся UUID файлов, а S3-ключи строятся из этих UUID: [DownloadFileCommandHandler.cs](../../Backend/BarkCloud.Files/Features/DownloadFile/DownloadFileCommandHandler.cs#L82-L110), [S3Uploader.cs](../../Backend/BarkCloud.Files/Infrastructure/S3Uploader.cs#L14-L31). Поэтому объекты можно перенести без изменения БД, если сохранить:

- соответствующий bucket (user-avatars / cloud-files или новые имена в Configuration);
- object key, равный UUID записи;
- байты объекта;
- Content-Type и metadata, где они нужны.

Безопасный порядок:

1. Создать два R2 bucket.
2. Создать scoped R2 API token с Object Read & Write.
3. Скопировать из MinIO все объекты обоих bucket в R2, сохранив UUID-ключи; отдельно проверить аватары, обычные файлы, превью и JpegView.
4. На короткое окно остановить записи или временно запретить загрузки, чтобы MinIO и R2 не разошлись.
5. Обновить восемь значений S3Buckets:* в Configuration DB.
6. Внести правку в S3Uploader, задеплоить Files и перезапустить его.
7. Проверить upload/download/delete/range, SHA-256 байтов и фоновые cleanup/backfill-сервисы.
8. Только после сверки отключать MinIO и удалять его данные.

## Если позже понадобятся прямые S3-ссылки

Сейчас они не нужны: клиенты ходят в HTTP API Files. Если позже захочется разгрузить Files и отдавать клиентам presigned PUT/GET непосредственно из R2, R2 это поддерживает для GET, HEAD, PUT и DELETE; срок presigned URL — от 1 секунды до 7 дней. Такие ссылки работают на S3 API domain и не работают с custom domain; для браузерного сценария понадобится CORS: [R2 presigned URLs](https://developers.cloudflare.com/r2/api/s3/presigned-urls/).

Это будет отдельный архитектурный этап: после прямой загрузки сервису понадобится подтверждать факт загрузки и размер/хеш объекта перед фиксацией UploadFile и генерацией превью.

## Рекомендуемый следующий тикет

Поддержать Cloudflare R2 в Files: добавить два флага в S3Uploader, явно задать AuthenticationRegion = "auto", покрыть registry/uploader интеграционным тестом с S3-compatible endpoint, добавить R2-поля в Builder и определить поведение storage-info при backend r2. После этого отдельно выполнить миграцию данных MinIO → R2.
