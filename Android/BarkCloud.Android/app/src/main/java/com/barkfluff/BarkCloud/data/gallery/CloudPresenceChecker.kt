package com.barkfluff.BarkCloud.data.gallery

import com.barkfluff.BarkCloud.data.cloud.CHECK_HASHES_BATCH
import com.barkfluff.BarkCloud.data.cloud.CloudCopy
import com.barkfluff.BarkCloud.data.cloud.HashPresence
import com.barkfluff.BarkCloud.data.cloud.checkPresenceBatched
import com.barkfluff.BarkCloud.data.persistence.MediaCloudStateDao
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Общий ограниченный конвейер присутствия файлов устройства в облаке (вынесен из GalleryViewModel).
 *
 * - Хеш берётся из `media_cloud_states.hash`, пока совпадают dateModified и size; иначе считается
 *   последовательно (одна очередь, один файл за раз). Полной индексации библиотеки нет: в работу
 *   попадают только запрошенные элементы (видимые ячейки, открытая папка).
 * - Проверка наличия идёт пакетами по [CHECK_HASHES_BATCH]. Ответ записывается условными UPDATE:
 *   QUEUED/UPLOADING не перетираются, поздний «нет в облаке» не понижает IN_CLOUD.
 * - CHECKING не сохраняется: отмена не оставляет постоянного состояния, а элемент можно запросить снова.
 *
 * [hashFile] и [presence] подставляются, чтобы логика тестировалась без Android и сети.
 */
internal class CloudPresenceChecker(
    private val scope: CoroutineScope,
    private val mediaDao: MediaCloudStateDao,
    private val hashFile: suspend (DeviceMedia) -> String?,
    private val presence: suspend (List<String>) -> Map<String, HashPresence>,
    private val now: () -> Long = System::currentTimeMillis,
    private val debounceMillis: Long = 400L,
    private val recheckAfterMillis: Long = 30_000L,
) {
    private val pending = LinkedHashMap<String, DeviceMedia>()
    private val lastChecked = HashMap<String, Long>()
    private val flushLock = Mutex()
    private val scheduled = AtomicBoolean(false)

    /**
     * Ставит элемент в очередь проверки. [force] — ревалидация при открытии папки или обновлении,
     * минуя окно [recheckAfterMillis] для уже проверенных элементов.
     */
    fun request(media: DeviceMedia, force: Boolean = false) {
        synchronized(pending) {
            if (!force && lastChecked[media.mediaKey]?.let { now() - it < recheckAfterMillis } == true) return
            pending[media.mediaKey] = media
        }
        if (scheduled.compareAndSet(false, true)) {
            scope.launch {
                delay(debounceMillis)
                scheduled.set(false)
                flush()
            }
        }
    }

    /** Один проход по накопленной очереди. Отмена не теряет элементы: очередь опустошается только при старте. */
    suspend fun flush() = flushLock.withLock {
        val batch = synchronized(pending) { pending.values.toList().also { pending.clear() } }
        if (batch.isEmpty()) return
        val hashed = batch.mapNotNull { media -> hashOf(media)?.let { media to it } }
        val mediaByHash = hashed.groupBy({ it.second }, { it.first })
        mediaByHash.keys.chunked(CHECK_HASHES_BATCH).forEach { chunk ->
            val answers = presence(chunk)
            chunk.forEach { hash ->
                val answer = answers[hash] ?: return@forEach // нет ответа — UNKNOWN, состояние не меняем
                mediaByHash.getValue(hash).forEach { media -> record(media, hash, answer) }
            }
        }
        val stamp = now()
        synchronized(pending) { hashed.forEach { (media, _) -> lastChecked[media.mediaKey] = stamp } }
    }

    /** Хеш файла: из кеша, если метаданные совпадают, иначе вычисляется. */
    suspend fun hashOf(media: DeviceMedia): String? {
        val row = mediaDao.byKey(media.mediaKey)
        if (row != null && row.dateModifiedSeconds == media.dateModifiedSeconds && row.sizeBytes == media.sizeBytes) {
            row.hash?.let { return it }
        } else if (row != null && row.status != MediaCloudStatus.QUEUED && row.status != MediaCloudStatus.UPLOADING) {
            // Метаданные изменились: прежние статус и хеш больше не относятся к файлу.
            mediaDao.upsert(cloudStateOf(media, hash = null, status = MediaCloudStatus.NOT_IN_CLOUD))
        }
        val hash = hashFile(media) ?: return null
        if (mediaDao.setHash(media.mediaKey, media.dateModifiedSeconds, media.sizeBytes, hash) == 0) {
            mediaDao.insertIfAbsent(cloudStateOf(media, hash, MediaCloudStatus.NOT_IN_CLOUD))
        }
        return hash
    }

    private suspend fun record(media: DeviceMedia, hash: String, answer: HashPresence) {
        if (answer.exists) {
            mediaDao.markInCloud(media.mediaKey, media.dateModifiedSeconds, media.sizeBytes, hash, answer.fileIds.firstOrNull())
        } else {
            mediaDao.markAbsent(media.mediaKey, media.dateModifiedSeconds, media.sizeBytes, hash)
        }
    }
}

/**
 * Состояние облачной копии каждого фото по хешу (хеш из кеша или вычисляется), затем пакетная
 * проверка. Фото без хеша (нечитаемый файл) получает [CloudCopy.Unknown].
 */
internal suspend fun resolveCloudCopies(
    items: List<DeviceMedia>,
    hashOf: suspend (DeviceMedia) -> String?,
    presence: suspend (List<String>) -> Map<String, HashPresence>,
): Map<String, CloudCopy> {
    val hashByKey = items.mapNotNull { item -> hashOf(item)?.let { item.mediaKey to it } }.toMap()
    val answers = checkPresenceBatched(hashByKey.values.toList()) { chunk -> presence(chunk) }
    return items.associate { item ->
        val hash = hashByKey[item.mediaKey]
        item.mediaKey to if (hash == null) CloudCopy.Unknown else CloudCopy.of(answers[hash])
    }
}
