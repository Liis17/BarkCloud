-- Восстановление после ручного создания videos-v2 и удаления исходного R2-бакета.
-- Выполнить целиком в БД Configuration только после подтверждения завершённого копирования.
-- Берёт credentials из videos-v2 внутри БД; не выводит их. После COMMIT перезапустить Files.
-- ID, версии, роли, активность, legacy-признаки и квоты сохраняются.
BEGIN;
SET LOCAL lock_timeout = '10s';
LOCK TABLE "StorageProfiles" IN SHARE ROW EXCLUSIVE MODE;

DO $recovery$
DECLARE
    source "StorageProfiles"%ROWTYPE;
    destination "StorageProfiles"%ROWTYPE;
    previous "StorageProfiles"%ROWTYPE;
    updated "StorageProfiles"%ROWTYPE;
    old_endpoint constant text := 'https://76671501a95f618c6ebe76ba9421a0cc.r2.cloudflarestorage.com';
    snapshot_excluded constant text[] := ARRAY['CreatedAt', 'CreatedBy', 'CreatedFrom', 'EditedAt', 'EditedBy', 'EditedFrom'];
    changed integer := 0;
BEGIN
    SELECT * INTO STRICT source FROM "StorageProfiles" WHERE "ProfileId" = 'videos-v1';
    SELECT * INTO STRICT destination FROM "StorageProfiles" WHERE "ProfileId" = 'videos-v2';
    IF destination."Role" <> 'videos' OR NOT destination."IsActive" OR destination."IsLegacy"
        OR lower(rtrim(destination."ServiceUrl", '/')) <> 'https://s3.fra.databucket.eu'
        OR destination."BucketName" <> 'cloud-video' OR destination."IsR2"
        OR btrim(destination."AccessKey") = '' OR btrim(destination."SecretKey") = '' THEN
        RAISE EXCEPTION 'videos-v2 не соответствует ожидаемому новому S3; изменения отменены';
    END IF;
    IF source."Role" <> 'videos' OR source."IsLegacy" OR NOT (
        (lower(rtrim(source."ServiceUrl", '/')) = old_endpoint AND source."BucketName" = 'cloud-video' AND source."IsR2")
        OR (source."ServiceUrl" = destination."ServiceUrl" AND source."BucketName" = destination."BucketName"
            AND source."AccessKey" = destination."AccessKey" AND source."SecretKey" = destination."SecretKey"
            AND source."IsR2" = destination."IsR2" AND source."Region" = destination."Region"
            AND source."ForcePathStyle" = destination."ForcePathStyle")
    ) THEN
        RAISE EXCEPTION 'videos-v1 изменён неожиданным образом; изменения отменены';
    END IF;

    -- Переключить также связанные версии/legacy того же физического исходного бакета.
    -- Для R2 клиент принудительно использует HTTPS без явно указанного порта.
    FOR previous IN SELECT * FROM "StorageProfiles" p WHERE p."BucketName" = 'cloud-video' AND (
        lower(rtrim(p."ServiceUrl", '/')) = old_endpoint
        OR (p."IsR2" AND lower(p."ServiceUrl") ~ '^https?://76671501a95f618c6ebe76ba9421a0cc\.r2\.cloudflarestorage\.com(:[0-9]+)?/?$')
    ) ORDER BY p."ProfileId"
    LOOP
        UPDATE "StorageProfiles" SET
            "ServiceUrl" = destination."ServiceUrl", "BucketName" = destination."BucketName",
            "AccessKey" = destination."AccessKey", "SecretKey" = destination."SecretKey",
            "IsR2" = destination."IsR2", "Region" = destination."Region",
            "ForcePathStyle" = destination."ForcePathStyle",
            "EditedAt" = transaction_timestamp(), "EditedBy" = 'admin',
            "EditedFrom" = 's3-recovery:videos-v1<-videos-v2'
        WHERE "ProfileId" = previous."ProfileId" RETURNING * INTO STRICT updated;
        INSERT INTO "StorageProfileRevisions"
            ("ProfileId", "PreviousValue", "NewValue", "ChangedAt", "ChangedBy", "ChangedFrom", "ChangeKind", "SourceRevisionId")
        VALUES (updated."ProfileId", (to_jsonb(previous) - snapshot_excluded)::text,
            (to_jsonb(updated) - snapshot_excluded)::text, transaction_timestamp(), 'admin',
            's3-recovery:videos-v1<-videos-v2', 'MigrationRecovery', NULL);
        changed := changed + 1;
    END LOOP;
    RAISE NOTICE 'Переключено профилей: %. Перезапустите Files.', changed;
END;
$recovery$;

COMMIT;

-- Результат без credentials. Повторный запуск не создаёт дублирующих ревизий.
SELECT "ProfileId", "Role", "Version", "ServiceUrl", "BucketName", "IsActive", "IsLegacy", "Region", "ForcePathStyle"
FROM "StorageProfiles"
WHERE "EditedFrom" = 's3-recovery:videos-v1<-videos-v2' OR "ProfileId" = 'videos-v2'
ORDER BY "ProfileId";
