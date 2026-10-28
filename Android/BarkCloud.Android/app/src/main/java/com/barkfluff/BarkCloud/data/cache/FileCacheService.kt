package com.barkfluff.BarkCloud.data.cache

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File
import java.io.IOException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

class FileCacheService(
    baseDirectory: File,
    private val index: FileCacheIndex,
    private val loader: FileCacheLoader,
    private val settings: FileCachePolicy,
    private val currentScope: () -> CacheScope,
    private val nowMillis: () -> Long = System::currentTimeMillis,
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO,
) {
    private val root = File(baseDirectory, "BarkCloudFiles")
    private val originalsDir = File(root, CacheCategory.ORIGINAL.directoryName)
    private val managedDir = File(root, "managed")
    private val previewsDir = File(managedDir, CacheCategory.PREVIEW.directoryName)
    private val jpegViewsDir = File(managedDir, CacheCategory.JPEG_VIEW.directoryName)
    private val legacyPreviewsDir = File(root, "previews")

    private val stateMutex = Mutex()
    private val epochGuard = Any()
    private val epoch = AtomicLong(0L)
    private val flights = ConcurrentHashMap<String, Flight>()
    private val pins = mutableMapOf<String, Int>()
    private var initialized = false
    private var clearing = false
    private var clearFinished = CompletableDeferred<Unit>().apply { complete(Unit) }
    private var latestTimestamp = 0L

    private data class Flight(
        val storageKey: String,
        val epoch: Long,
        val ownerJob: Job?,
        val result: CompletableDeferred<File> = CompletableDeferred(),
    )

    /** Возвращает кешированный файл и обновляет lastUsedAt. */
    suspend fun get(key: FileCacheKey): File? = withContext(ioDispatcher) {
        ensureInitialized()
        val operationEpoch = epoch.get()
        withCacheState {
            ensureCurrent(key, operationEpoch)
            findFileLocked(key)
        }
    }

    /** Загружает файл при промахе кеша; параллельные запросы одного ключа используют одну загрузку. */
    suspend fun load(
        key: FileCacheKey,
        fileName: String,
        sourceUrl: String? = null,
        sourceFile: File? = null,
    ): File = withContext(ioDispatcher) {
        ensureInitialized()
        val operationEpoch = withCacheState { epoch.get() }
        val request = FileCacheLoadRequest(key, fileName, sourceUrl, sourceFile)
        val cached = withCacheState {
            ensureCurrent(key, operationEpoch)
            findFileLocked(key)
        }
        if (cached != null) return@withContext cached

        val (flight, isOwner) = withCacheState {
            ensureCurrent(key, operationEpoch)
            while (true) {
                val existing = flights[key.storageKey]
                if (existing != null && existing.epoch == operationEpoch) {
                    return@withCacheState existing to false
                }
                if (existing != null) {
                    flights.remove(key.storageKey, existing)
                    cancelFlight(existing, "Кеш изменился во время загрузки")
                }
                val candidate = Flight(
                    storageKey = key.storageKey,
                    epoch = operationEpoch,
                    ownerJob = currentCoroutineContext()[Job],
                )
                val active = synchronized(epochGuard) {
                    if (epoch.get() != operationEpoch) throw CancellationException("Кеш изменился во время загрузки")
                    flights.putIfAbsent(key.storageKey, candidate)
                }
                if (active == null) {
                    pins[key.storageKey] = (pins[key.storageKey] ?: 0) + 1
                    if (epoch.get() != operationEpoch) {
                        flights.remove(key.storageKey, candidate)
                        unpinLocked(key.storageKey)
                        cancelFlight(candidate, "Кеш изменился во время загрузки")
                        throw CancellationException("Кеш изменился во время загрузки")
                    }
                    return@withCacheState candidate to true
                }
            }
            error("unreachable")
        }

        if (!isOwner) {
            val result = flight.result.await()
            withCacheState {
                ensureCurrent(key, operationEpoch)
                findFileLocked(key) ?: throw CancellationException("Файл был удалён из кеша")
            }
            return@withContext result
        }

        val target = cacheFile(key, cacheFileName(key, fileName))
        val temporary = File(target.parentFile, ".${target.name}.${UUID.randomUUID()}.tmp")
        try {
            target.parentFile?.mkdirs()
            loader.load(request, temporary)
            currentCoroutineContext().ensureActive()
            if (!temporary.isFile || temporary.length() <= 0L) {
                throw IOException("Загрузчик не создал полный файл кеша")
            }

            withCacheState {
                ensureCurrent(key, operationEpoch)
                Files.move(
                    temporary.toPath(),
                    target.toPath(),
                    StandardCopyOption.ATOMIC_MOVE,
                    StandardCopyOption.REPLACE_EXISTING,
                )
                val entry = FileCacheEntry(
                    key = key,
                    sizeBytes = target.length(),
                    lastUsedAt = nextTimestampLocked(),
                    fileName = target.name,
                )
                try {
                    index.upsert(entry)
                    ensureCurrent(key, operationEpoch)
                    evictLocked()
                    synchronized(epochGuard) {
                        ensureCurrent(key, operationEpoch)
                        flight.result.complete(target)
                    }
                    target
                } catch (error: Throwable) {
                    index.delete(key.storageKey)
                    target.delete()
                    throw error
                }
            }
        } catch (error: Throwable) {
            flight.result.completeExceptionally(error)
            throw error
        } finally {
            temporary.delete()
            stateMutex.withLock {
                flights.remove(key.storageKey, flight)
                unpinLocked(key.storageKey)
            }
        }
    }

    /** Совместимый API для существующих вызовов открытия оригинала. */
    suspend fun loadOriginal(fileId: String, fileName: String): File =
        load(FileCacheKey.original(currentScope(), fileId), fileName)

    /** Загрузка файла из общей папки с ключом, отдельным от собственного оригинала. */
    suspend fun loadSharedOriginal(fileId: String, fileName: String, sourceUrl: String): File =
        load(FileCacheKey.sharedOriginal(currentScope(), fileId), fileName, sourceUrl)

    /** Удерживает оригинал в кеше, пока внешний просмотрщик запускается. */
    suspend fun <T> withPinnedOriginal(fileId: String, action: suspend (File) -> T): T =
        withPinnedEntry(FileCacheKey.original(currentScope(), fileId), action)

    /** Пин удерживает запись от LRU-вытеснения до соответствующего unpin. */
    suspend fun pin(key: FileCacheKey): File? = withContext(ioDispatcher) {
        ensureInitialized()
        val operationEpoch = epoch.get()
        withCacheState {
            ensureCurrent(key, operationEpoch)
            val file = findFileLocked(key) ?: return@withCacheState null
            pins[key.storageKey] = (pins[key.storageKey] ?: 0) + 1
            file
        }
    }

    suspend fun unpin(key: FileCacheKey) = withContext(ioDispatcher) {
        withCacheState {
            unpinLocked(key.storageKey)
            evictLocked()
        }
    }

    suspend fun <T> withPinnedEntry(key: FileCacheKey, action: suspend (File) -> T): T {
        val file = pin(key) ?: throw IOException("Файл отсутствует в кеше")
        try {
            return action(file)
        } finally {
            unpin(key)
        }
    }

    suspend fun statistics(): FileCacheStats = withContext(ioDispatcher) {
        ensureInitialized()
        val entries = withCacheState { index.all() }
        val categories = CacheCategory.values().associateWith { category ->
            val matching = entries.filter { it.key.category == category }
            CacheCategoryStats(matching.sumOf { it.sizeBytes }, matching.size)
        }
        val legacyFiles = filesIn(legacyPreviewsDir)
        val total = entries.sumOf { it.sizeBytes }
        FileCacheStats(
            categories = categories,
            totalSizeBytes = total,
            entryCount = entries.size,
            isOverLimit = total > settings.maxCacheBytes,
            legacyPreviewSizeBytes = legacyFiles.sumOf { it.length() },
            legacyPreviewEntryCount = legacyFiles.size,
        )
    }

    suspend fun totalSize(): Long = statistics().totalSizeBytes

    suspend fun entryCount(): Int = statistics().entryCount

    /** Размер и количество превью в отдельном legacy disk cache Coil. */
    suspend fun previewSize(): Long = withContext(ioDispatcher) {
        filesIn(legacyPreviewsDir).sumOf { it.length() }
    }

    suspend fun previewEntryCount(): Int = withContext(ioDispatcher) {
        filesIn(legacyPreviewsDir).size
    }

    suspend fun clearAll() = withContext(ioDispatcher) {
        val (gate, activeFlights) = beginClear()
        activeFlights.forEach { cancelFlight(it, "Кеш очищен") }
        try {
            originalsDir.deleteRecursively()
            managedDir.deleteRecursively()
            originalsDir.mkdirs()
            LocalShareFileCache.clear(root.parentFile ?: root)
            index.clear()
            initialized = true
            latestTimestamp = nowMillis()
        } finally {
            stateMutex.withLock { clearing = false }
            gate.complete(Unit)
        }
    }

    suspend fun clearStale(): Int = withContext(ioDispatcher) {
        ensureInitialized()
        withCacheState { clearStaleLocked(nowMillis()) }
    }

    suspend fun runStartupSweepIfNeeded() = withContext(ioDispatcher) {
        ensureInitialized()
        withCacheState {
            val now = nowMillis()
            if (now - settings.lastSweepAtMillis < FileCacheSettings.DAY_MILLIS) return@withCacheState
            clearStaleLocked(now)
            evictLocked()
            settings.lastSweepAtMillis = now
        }
    }

    suspend fun enforceSizeLimit() = withContext(ioDispatcher) {
        ensureInitialized()
        withCacheState { evictLocked() }
    }

    /** Вызывается при смене сервера; старые операции отменяются и не пройдут epoch-check. */
    fun cancelInFlightLoads() {
        val active = synchronized(epochGuard) {
            val currentEpoch = epoch.incrementAndGet()
            flights.values.filter { it.epoch < currentEpoch }
        }
        active.forEach { flight ->
            flights.remove(flight.storageKey, flight)
            cancelFlight(flight, "Область кеша изменилась")
        }
    }

    private suspend fun ensureInitialized() {
        withCacheState {
            if (initialized) return@withCacheState
            originalsDir.mkdirs()
            previewsDir.mkdirs()
            jpegViewsDir.mkdirs()

            val entries = index.all()
            val presentNames = mutableSetOf<Pair<CacheCategory, String>>()
            entries.forEach { entry ->
                val file = resolveFile(entry)
                if (file == null || !file.isFile || file.length() <= 0L) {
                    if (file?.isFile == true && file.length() <= 0L) file.delete()
                    index.delete(entry.key.storageKey)
                } else {
                    presentNames += entry.key.category to entry.fileName
                }
            }

            listOf(originalsDir, previewsDir, jpegViewsDir).forEach { dir ->
                filesIn(dir).filter { it.name.startsWith(".") && it.name.endsWith(".tmp") }.forEach(File::delete)
            }

            latestTimestamp = maxOf(latestTimestamp, entries.maxOfOrNull { it.lastUsedAt } ?: 0L)
            val scope = currentScope()
            filesIn(originalsDir)
                .filterNot { it.name.startsWith(".") && it.name.endsWith(".tmp") }
                .filterNot { (CacheCategory.ORIGINAL to it.name) in presentNames }
                .forEach { file ->
                    if (file.length() <= 0L) {
                        file.delete()
                        return@forEach
                    }
                    val fileId = originalFileId(file.name) ?: return@forEach
                    val key = FileCacheKey.original(scope, fileId)
                    val adoptedAt = nextTimestampLocked()
                    index.upsert(
                        FileCacheEntry(
                            key = key,
                            sizeBytes = file.length(),
                            lastUsedAt = adoptedAt,
                            fileName = file.name,
                        ),
                    )
                }
            initialized = true
        }
    }

    private suspend fun findFileLocked(key: FileCacheKey): File? {
        val entry = index.get(key.storageKey) ?: return null
        val file = resolveFile(entry)
        if (file == null || !file.isFile || file.length() <= 0L) {
            file?.delete()
            index.delete(key.storageKey)
            return null
        }
        val age = settings.staleMaxAgeMillis
        if (age != FileCacheSettings.NEVER && nowMillis() - entry.lastUsedAt > age) {
            if (pins[key.storageKey] == null) {
                file.delete()
                index.delete(key.storageKey)
                return null
            }
        }
        index.upsert(entry.copy(sizeBytes = file.length(), lastUsedAt = nextTimestampLocked(entry.lastUsedAt)))
        return file
    }

    private suspend fun clearStaleLocked(now: Long): Int {
        val age = settings.staleMaxAgeMillis
        if (age == FileCacheSettings.NEVER) return 0
        val cutoff = now - age
        var removed = 0
        index.all()
            .filter { it.lastUsedAt < cutoff && pins[it.key.storageKey] == null }
            .forEach { entry ->
                resolveFile(entry)?.delete()
                index.delete(entry.key.storageKey)
                removed++
            }
        return removed
    }

    private suspend fun evictLocked() {
        val entries = index.all().sortedWith(compareBy<FileCacheEntry> { it.lastUsedAt }.thenBy { it.key.storageKey })
        var total = entries.sumOf { it.sizeBytes }
        val max = settings.maxCacheBytes
        if (total <= max || entries.isEmpty()) return
        val survivor = entries.maxWithOrNull(compareBy<FileCacheEntry> { it.lastUsedAt }.thenBy { it.key.storageKey })
        for (entry in entries) {
            if (total <= max) break
            val id = entry.key.storageKey
            if (entry == survivor || pins[id] != null || flights.containsKey(id)) continue
            val file = resolveFile(entry)
            if (file != null && file.exists() && !file.delete()) continue
            index.delete(id)
            total -= entry.sizeBytes
        }
    }

    private fun nextTimestampLocked(previous: Long = 0L): Long {
        latestTimestamp = maxOf(nowMillis(), latestTimestamp + 1L, previous + 1L)
        return latestTimestamp
    }

    private fun ensureCurrent(key: FileCacheKey, operationEpoch: Long) {
        if (epoch.get() != operationEpoch || currentScope() != key.scope) {
            throw CancellationException("Область кеша изменилась")
        }
    }

    private suspend fun <T> withCacheState(action: suspend () -> T): T {
        while (true) {
            stateMutex.lock()
            val waitForClear = if (clearing) clearFinished else null
            if (waitForClear == null) {
                try {
                    return action()
                } finally {
                    stateMutex.unlock()
                }
            }
            stateMutex.unlock()
            waitForClear.await()
        }
    }

    private suspend fun beginClear(): Pair<CompletableDeferred<Unit>, List<Flight>> {
        while (true) {
            stateMutex.lock()
            if (clearing) {
                val waitForClear = clearFinished
                stateMutex.unlock()
                waitForClear.await()
                continue
            }
            clearing = true
            val gate = CompletableDeferred<Unit>()
            clearFinished = gate
            synchronized(epochGuard) { epoch.incrementAndGet() }
            val active = flights.values.toList()
            flights.clear()
            pins.clear()
            stateMutex.unlock()
            return gate to active
        }
    }

    private fun cancelFlight(flight: Flight, message: String) {
        flight.ownerJob?.cancel(CancellationException(message))
        flight.result.cancel(CancellationException(message))
    }

    private fun unpinLocked(storageKey: String) {
        val count = pins[storageKey] ?: return
        if (count <= 1) pins.remove(storageKey) else pins[storageKey] = count - 1
    }

    private fun resolveFile(entry: FileCacheEntry): File? {
        if (File(entry.fileName).name != entry.fileName) return null
        return cacheFile(entry.key, entry.fileName)
    }

    private fun cacheFile(key: FileCacheKey, fileName: String): File =
        File(directory(key.category), fileName)

    private fun directory(category: CacheCategory): File = when (category) {
        CacheCategory.ORIGINAL -> originalsDir
        CacheCategory.PREVIEW -> previewsDir
        CacheCategory.JPEG_VIEW -> jpegViewsDir
    }

    private fun cacheFileName(key: FileCacheKey, fileName: String): String {
        val extension = fileName.substringAfterLast('.', "")
            .takeIf { it.length in 1..12 && it.all(Char::isLetterOrDigit) }
            ?.let { ".$it" }
            .orEmpty()
        val encodedId = Base64.getUrlEncoder().withoutPadding()
            .encodeToString(key.fileId.toByteArray(Charsets.UTF_8))
        return "entry-$encodedId.${key.storageKey}$extension"
    }

    private fun originalFileId(fileName: String): String? {
        if (fileName.startsWith("entry-")) {
            val encoded = fileName.removePrefix("entry-").substringBefore('.')
            return runCatching { String(Base64.getUrlDecoder().decode(encoded), Charsets.UTF_8) }
                .getOrNull()
                ?.takeIf { it.isNotBlank() }
        }
        return fileName.substringBeforeLast('.', fileName).takeIf { it.isNotBlank() }
    }

    private fun filesIn(directory: File): List<File> =
        directory.walkTopDown().filter { it.isFile }.toList()
}
