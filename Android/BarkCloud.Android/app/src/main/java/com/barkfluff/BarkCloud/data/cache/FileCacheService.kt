package com.barkfluff.BarkCloud.data.cache

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
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
    // Accessed under stateMutex; keep warm preview reads out of Room's query queue.
    private val recentEntries = object : java.util.LinkedHashMap<String, FileCacheEntry>(256, 0.75f, true) {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, FileCacheEntry>?): Boolean =
            size > 256
    }
    private val loaderScope = CoroutineScope(SupervisorJob() + ioDispatcher)
    private val epochGuard = Any()
    private val epoch = AtomicLong(0L)
    private val flights = ConcurrentHashMap<String, Flight>()
    private val pins = mutableMapOf<String, MutableSet<Long>>()
    private val pinKeys = mutableMapOf<Long, String>()
    private val nextPinToken = AtomicLong(0L)
    private val reboundAccounts = ConcurrentHashMap<String, String>()
    private var initialized = false
    private var clearing = false
    private var clearFinished = CompletableDeferred<Unit>().apply { complete(Unit) }
    private var latestTimestamp = 0L

    private data class Flight(
        @Volatile var storageKey: String,
        @Volatile var key: FileCacheKey,
        val epoch: Long,
        val result: CompletableDeferred<File> = CompletableDeferred(),
        val usersDrained: CompletableDeferred<Unit> = CompletableDeferred(),
        @Volatile var users: Int = 0,
        var pinToken: Long = 0L,
        var job: Job? = null,
    )

    private data class LoadedFile(val file: File, val pin: FileCachePin?)

    private data class LoadSelection(
        val cached: LoadedFile? = null,
        val flight: Flight? = null,
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
    ): File = loadInternal(key, fileName, sourceUrl, sourceFile, pinResult = false).file

    private suspend fun loadInternal(
        key: FileCacheKey,
        fileName: String,
        sourceUrl: String? = null,
        sourceFile: File? = null,
        pinResult: Boolean,
        onPinAcquired: ((FileCachePin) -> Unit)? = null,
    ): LoadedFile = withContext(ioDispatcher) {
        ensureInitialized()
        val operationEpoch = withCacheState { epoch.get() }
        val request = FileCacheLoadRequest(key, fileName, sourceUrl, sourceFile)

        while (true) {
            val selection = withCacheState {
                ensureCurrent(key, operationEpoch)
                val activeKey = canonicalKey(key)
                val cached = findFileLocked(activeKey)
                if (cached != null) {
                    val pin = if (pinResult) {
                        FileCachePin(addPinLocked(activeKey.storageKey)).also { onPinAcquired?.invoke(it) }
                    } else {
                        null
                    }
                    return@withCacheState LoadSelection(cached = LoadedFile(cached, pin))
                }

                val existing = flights[activeKey.storageKey]
                    ?: flights.values.firstOrNull {
                        it.epoch == operationEpoch && canonicalKey(it.key) == activeKey
                    }
                if (existing != null && existing.result.isCancelled) {
                    flights.remove(existing.storageKey, existing)
                    return@withCacheState null
                }
                if (existing != null) {
                    existing.users++
                    return@withCacheState LoadSelection(flight = existing)
                }

                val candidate = Flight(
                    storageKey = activeKey.storageKey,
                    key = activeKey,
                    epoch = operationEpoch,
                )
                candidate.users = 1
                synchronized(epochGuard) {
                    if (epoch.get() != operationEpoch) {
                        throw CacheInvalidatedException("Область кеша изменилась")
                    }
                    flights[candidate.storageKey] = candidate
                    candidate.pinToken = addPinLocked(candidate.storageKey)
                }
                candidate.job = loaderScope.launch(
                    start = CoroutineStart.UNDISPATCHED,
                ) {
                    performFlight(candidate, request, cacheFile(activeKey, cacheFileName(activeKey, fileName)))
                }
                candidate.job?.invokeOnCompletion { cause ->
                    if (cause is CancellationException && !candidate.result.isCompleted) {
                        candidate.result.cancel(cause)
                    }
                }
                LoadSelection(flight = candidate)
            } ?: continue

            val cached = selection.cached
            if (cached != null) return@withContext cached
            val flight = requireNotNull(selection.flight)

            try {
                flight.result.await()
                val loaded = withCacheState {
                    ensureCurrent(key, operationEpoch)
                    val activeKey = canonicalKey(key)
                    val file = findFileLocked(activeKey)
                    if (file == null) {
                        flights.remove(flight.storageKey, flight)
                        null
                    } else {
                        val pin = if (pinResult) {
                            FileCachePin(addPinLocked(activeKey.storageKey)).also { onPinAcquired?.invoke(it) }
                        } else {
                            null
                        }
                        LoadedFile(file, pin)
                    }
                }
                if (loaded != null) return@withContext loaded
            } catch (error: CancellationException) {
                if (!currentCoroutineContext().isActive) throw error
                if (!flight.result.isCancelled) throw error
                // Владелец отменился сам; активный ожидающий займёт освободившийся flight.
            } finally {
                withContext(NonCancellable) {
                    stateMutex.withLock {
                        flight.users--
                        if (flight.users == 0) {
                            flight.usersDrained.complete(Unit)
                            if (!flight.result.isCompleted) {
                                flight.job?.cancel(CancellationException("Нет ожидающих загрузку"))
                            }
                        }
                    }
                }
            }
        }
        error("unreachable")
    }

    private suspend fun performFlight(flight: Flight, request: FileCacheLoadRequest, target: File) {
        val temporary = File(target.parentFile, ".${target.name}.${UUID.randomUUID()}.tmp")
        try {
            withCacheState { ensureCurrent(flight.key, flight.epoch) }
            target.parentFile?.mkdirs()
            loader.load(request, temporary)
            currentCoroutineContext().ensureActive()
            if (!temporary.isFile || temporary.length() <= 0L) {
                throw IOException("Загрузчик не создал полный файл кеша")
            }

            withCacheState {
                ensureCurrent(flight.key, flight.epoch)
                val activeKey = canonicalKey(flight.key)
                moveFlightToKeyLocked(flight, activeKey)
                Files.move(
                    temporary.toPath(),
                    target.toPath(),
                    StandardCopyOption.ATOMIC_MOVE,
                    StandardCopyOption.REPLACE_EXISTING,
                )
                val entry = FileCacheEntry(
                    key = activeKey,
                    sizeBytes = target.length(),
                    lastUsedAt = nextTimestampLocked(),
                    fileName = target.name,
                )
                try {
                    upsertEntryLocked(entry)
                    ensureCurrent(activeKey, flight.epoch)
                    evictLocked()
                    synchronized(epochGuard) {
                        ensureCurrent(activeKey, flight.epoch)
                        flight.result.complete(target)
                    }
                } catch (error: Throwable) {
                    deleteEntryLocked(activeKey.storageKey)
                    target.delete()
                    throw error
                }
            }
            withContext(NonCancellable) { flight.usersDrained.await() }
        } catch (error: Throwable) {
            if (!flight.result.isCompleted) {
                if (error is CancellationException) flight.result.cancel(error)
                else flight.result.completeExceptionally(error)
            }
        } finally {
            withContext(NonCancellable) {
                temporary.delete()
                stateMutex.withLock {
                    flights.remove(flight.storageKey, flight)
                    removePinLocked(flight.pinToken)
                }
            }
        }
    }

    /** Совместимый API для существующих вызовов открытия оригинала. */
    suspend fun loadOriginal(fileId: String, fileName: String): File =
        load(FileCacheKey.original(currentScope(), fileId), fileName)

    suspend fun getOriginal(fileId: String): File? =
        get(FileCacheKey.original(currentScope(), fileId))

    /** Загрузка файла из общей папки с ключом, отдельным от собственного оригинала. */
    suspend fun loadSharedOriginal(fileId: String, fileName: String, sourceUrl: String): File =
        load(FileCacheKey.sharedOriginal(currentScope(), fileId), fileName, sourceUrl)

    /** Удерживает оригинал в кеше, пока внешний просмотрщик запускается. */
    suspend fun <T> withPinnedOriginal(fileId: String, action: suspend (File) -> T): T =
        withPinnedEntry(FileCacheKey.original(currentScope(), fileId), action)

    suspend fun getJpegView(fileId: String, jpegViewFileId: String? = null): File? =
        get(FileCacheKey.jpegView(currentScope(), fileId, jpegViewFileId))

    suspend fun loadJpegView(fileId: String, sourceUrl: String, jpegViewFileId: String? = null): File =
        load(FileCacheKey.jpegView(currentScope(), fileId, jpegViewFileId), "jpeg-view.jpg", sourceUrl)

    suspend fun <T> withPinnedJpegView(
        fileId: String,
        jpegViewFileId: String? = null,
        action: suspend (File) -> T,
    ): T = withPinnedEntry(FileCacheKey.jpegView(currentScope(), fileId, jpegViewFileId), action)

    suspend fun <T> withLoadedPinnedOriginal(
        fileId: String,
        fileName: String,
        action: suspend (File) -> T,
    ): T = withLoadedPinnedEntry(FileCacheKey.original(currentScope(), fileId), fileName, action = action)

    suspend fun <T> withLoadedPinnedJpegView(
        fileId: String,
        sourceUrl: String,
        jpegViewFileId: String? = null,
        action: suspend (File) -> T,
    ): T = withLoadedPinnedEntry(
        FileCacheKey.jpegView(currentScope(), fileId, jpegViewFileId),
        "jpeg-view.jpg",
        sourceUrl = sourceUrl,
        action = action,
    )

    suspend fun <T> withLoadedPinnedSharedOriginal(
        fileId: String,
        fileName: String,
        sourceUrl: String,
        action: suspend (File) -> T,
    ): T = withLoadedPinnedEntry(
        FileCacheKey.sharedOriginal(currentScope(), fileId),
        fileName,
        sourceUrl = sourceUrl,
        action = action,
    )

    /** Пин удерживает запись от LRU-вытеснения до соответствующего unpin. */
    suspend fun pin(key: FileCacheKey): PinnedFile? = pin(key, null)

    private suspend fun pin(
        key: FileCacheKey,
        onPinAcquired: ((FileCachePin) -> Unit)?,
    ): PinnedFile? = withContext(ioDispatcher) {
        ensureInitialized()
        val operationEpoch = epoch.get()
        withCacheState {
            ensureCurrent(key, operationEpoch)
            val activeKey = canonicalKey(key)
            val file = findFileLocked(activeKey) ?: return@withCacheState null
            val pin = FileCachePin(addPinLocked(activeKey.storageKey)).also { onPinAcquired?.invoke(it) }
            PinnedFile(file, pin)
        }
    }

    suspend fun unpin(pin: FileCachePin) = withContext(NonCancellable) {
        withContext(ioDispatcher) {
            withCacheState {
                removePinLocked(pin.token)
                evictLocked()
            }
        }
    }

    suspend fun <T> withPinnedEntry(key: FileCacheKey, action: suspend (File) -> T): T {
        var acquiredPin: FileCachePin? = null
        try {
            val pinned = pin(key) { acquiredPin = it } ?: throw IOException("Файл отсутствует в кеше")
            acquiredPin = pinned.pin
            return action(pinned.file)
        } finally {
            acquiredPin?.let { unpin(it) }
        }
    }

    private suspend fun <T> withLoadedPinnedEntry(
        key: FileCacheKey,
        fileName: String,
        sourceUrl: String? = null,
        action: suspend (File) -> T,
    ): T {
        var acquiredPin: FileCachePin? = null
        try {
            val loaded = loadInternal(
                key,
                fileName,
                sourceUrl,
                pinResult = true,
                onPinAcquired = { acquiredPin = it },
            )
            val pin = requireNotNull(loaded.pin)
            acquiredPin = pin
            return action(loaded.file)
        } finally {
            acquiredPin?.let { unpin(it) }
        }
    }

    suspend fun statistics(): FileCacheStats = withContext(ioDispatcher) {
        ensureInitialized()
        val entries = withCacheState { index.all() }
        val categories = CacheCategory.values().associateWith { category ->
            val matching = entries.filter { it.key.category == category }
            CacheCategoryStats(matching.sumOf { it.sizeBytes }, matching.size)
        }
        val legacyFiles = legacyPreviewFiles()
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
        legacyPreviewFiles().sumOf { it.length() }
    }

    suspend fun previewEntryCount(): Int = withContext(ioDispatcher) {
        legacyPreviewFiles().size
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
            recentEntries.clear()
            initialized = true
            latestTimestamp = nowMillis()
        } finally {
            withContext(NonCancellable) {
                stateMutex.withLock { clearing = false }
                gate.complete(Unit)
            }
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

    suspend fun invalidateOriginal(fileId: String) =
        invalidate(FileCacheKey.original(currentScope(), fileId))

    suspend fun invalidateJpegView(fileId: String, jpegViewFileId: String? = null) =
        invalidate(FileCacheKey.jpegView(currentScope(), fileId, jpegViewFileId))

    private suspend fun invalidate(key: FileCacheKey) = withContext(ioDispatcher) {
        ensureInitialized()
        val operationEpoch = epoch.get()
        withCacheState {
            ensureCurrent(key, operationEpoch)
            val activeKey = canonicalKey(key)
            val entry = getEntryLocked(activeKey.storageKey) ?: return@withCacheState
            resolveFile(entry)?.delete()
            deleteEntryLocked(activeKey.storageKey)
        }
    }

    /** Вызывается при смене сервера; старые операции отменяются и не пройдут epoch-check. */
    fun cancelInFlightLoads() {
        val active = synchronized(epochGuard) {
            val currentEpoch = epoch.incrementAndGet()
            reboundAccounts.clear()
            flights.values.filter { it.epoch < currentEpoch }
        }
        active.forEach { flight ->
            flights.remove(flight.storageKey, flight)
            cancelFlight(flight, "Область кеша изменилась")
        }
    }

    fun prepareUnknownAccountRebind(serverKey: String, accountId: String) {
        reboundAccounts[serverKey] = accountId
    }

    suspend fun rebindUnknownAccount(serverKey: String, accountId: String) = withContext(ioDispatcher) {
        val oldScope = CacheScope(serverKey, CacheScope.UNKNOWN_ACCOUNT)
        val newScope = CacheScope(serverKey, accountId)
        if (currentScope() != newScope) return@withContext
        val operationEpoch = epoch.get()
        reboundAccounts[serverKey] = accountId
        ensureInitialized()
        withCacheState {
            if (epoch.get() != operationEpoch || currentScope() != newScope) return@withCacheState
            val oldEntries = index.entriesInScope(oldScope)
            recentEntries.clear()
            index.rebindScope(oldScope, newScope)
            oldEntries.forEach { entry ->
                movePinsLocked(entry.key.storageKey, entry.key.copy(scope = newScope).storageKey)
            }
            if (epoch.get() != operationEpoch || currentScope() != newScope) return@withCacheState
            flights.values.toList().forEach { flight ->
                if (flight.key.scope == oldScope) moveFlightToKeyLocked(flight, flight.key.copy(scope = newScope))
            }
        }
    }

    fun accountIdentityChanged(serverKey: String, previousAccountId: String?, nextAccountId: String?) {
        when {
            nextAccountId == null -> cancelInFlightLoads()
            previousAccountId == null -> prepareUnknownAccountRebind(serverKey, nextAccountId)
            previousAccountId != nextAccountId -> cancelInFlightLoads()
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
                    deleteEntryLocked(entry.key.storageKey)
                } else {
                    presentNames += entry.key.category to entry.fileName
                }
            }

            listOf(CacheCategory.PREVIEW to previewsDir, CacheCategory.JPEG_VIEW to jpegViewsDir).forEach { (category, dir) ->
                filesIn(dir)
                    .filterNot { file -> (category to file.name) in presentNames }
                    .forEach(File::delete)
            }
            filesIn(originalsDir)
                .filter { it.name.startsWith(".") && it.name.endsWith(".tmp") }
                .forEach(File::delete)

            latestTimestamp = maxOf(latestTimestamp, entries.maxOfOrNull { it.lastUsedAt } ?: 0L)
            val scope = currentScope()
            filesIn(originalsDir)
                .filterNot { it.name.startsWith(".") && it.name.endsWith(".tmp") }
                .filterNot { (CacheCategory.ORIGINAL to it.name) in presentNames }
                .forEach { file ->
                    if (file.name.startsWith("entry-")) {
                        file.delete()
                        return@forEach
                    }
                    if (file.length() <= 0L) {
                        file.delete()
                        return@forEach
                    }
                    val fileId = originalFileId(file.name) ?: return@forEach
                    val key = FileCacheKey.original(scope, fileId)
                    val adoptedAt = nextTimestampLocked()
                    upsertEntryLocked(
                        FileCacheEntry(
                            key = key,
                            sizeBytes = file.length(),
                            lastUsedAt = adoptedAt,
                            fileName = file.name,
                        ),
                    )
                }
            if (scope.accountId != CacheScope.UNKNOWN_ACCOUNT) {
                recentEntries.clear()
                index.rebindScope(CacheScope(scope.serverKey, CacheScope.UNKNOWN_ACCOUNT), scope)
            }
            initialized = true
        }
    }

    private suspend fun getEntryLocked(storageKey: String): FileCacheEntry? =
        recentEntries[storageKey] ?: index.get(storageKey)?.also { recentEntries[storageKey] = it }

    private suspend fun upsertEntryLocked(entry: FileCacheEntry) {
        index.upsert(entry)
        recentEntries[entry.key.storageKey] = entry
    }

    private suspend fun deleteEntryLocked(storageKey: String) {
        index.delete(storageKey)
        recentEntries.remove(storageKey)
    }

    private suspend fun findFileLocked(key: FileCacheKey): File? {
        val activeKey = canonicalKey(key)
        var entry = getEntryLocked(activeKey.storageKey)
        if (entry == null && activeKey.scope.accountId == reboundAccounts[activeKey.scope.serverKey]) {
            val unknownKey = activeKey.copy(scope = activeKey.scope.copy(accountId = CacheScope.UNKNOWN_ACCOUNT))
            val unknownEntry = getEntryLocked(unknownKey.storageKey)
            if (unknownEntry != null) {
                entry = unknownEntry.copy(key = activeKey)
                deleteEntryLocked(unknownKey.storageKey)
                upsertEntryLocked(entry)
                movePinsLocked(unknownKey.storageKey, activeKey.storageKey)
            }
        }
        entry ?: return null
        val file = resolveFile(entry)
        if (file == null || !file.isFile || file.length() <= 0L) {
            file?.delete()
            deleteEntryLocked(activeKey.storageKey)
            return null
        }
        val age = settings.staleMaxAgeMillis
        if (age != FileCacheSettings.NEVER && nowMillis() - entry.lastUsedAt > age) {
            if (pins[activeKey.storageKey].isNullOrEmpty()) {
                file.delete()
                deleteEntryLocked(activeKey.storageKey)
                return null
            }
        }
        val now = nowMillis()
        val touchedAt = if (now - entry.lastUsedAt >= LRU_TOUCH_INTERVAL_MILLIS) {
            nextTimestampLocked(entry.lastUsedAt)
        } else {
            entry.lastUsedAt
        }
        if (entry.sizeBytes != file.length() || touchedAt != entry.lastUsedAt) {
            upsertEntryLocked(entry.copy(sizeBytes = file.length(), lastUsedAt = touchedAt))
        }
        return file
    }

    private suspend fun clearStaleLocked(now: Long): Int {
        val age = settings.staleMaxAgeMillis
        if (age == FileCacheSettings.NEVER) return 0
        val cutoff = now - age
        var removed = 0
        index.all()
            .filter { it.lastUsedAt < cutoff && pins[it.key.storageKey].isNullOrEmpty() }
            .forEach { entry ->
                resolveFile(entry)?.delete()
                deleteEntryLocked(entry.key.storageKey)
                removed++
            }
        return removed
    }

    private suspend fun evictLocked() {
        var total = index.totalSizeBytes()
        val max = settings.maxCacheBytes
        if (total <= max) return
        val candidates = index.evictionCandidates()
        val survivor = candidates.lastOrNull()?.key?.storageKey ?: return
        for (entry in candidates) {
            if (total <= max) break
            val id = entry.key.storageKey
            if (id == survivor || !pins[id].isNullOrEmpty() || flights.containsKey(id)) continue
            val file = resolveFile(entry)
            if (file != null && file.exists() && !file.delete()) continue
            deleteEntryLocked(id)
            total -= entry.sizeBytes
        }
    }

    private fun nextTimestampLocked(previous: Long = 0L): Long {
        latestTimestamp = maxOf(nowMillis(), latestTimestamp + 1L, previous + 1L)
        return latestTimestamp
    }

    private fun ensureCurrent(key: FileCacheKey, operationEpoch: Long) {
        if (epoch.get() != operationEpoch || !isCurrentScope(key)) {
            throw CacheInvalidatedException("Область кеша изменилась")
        }
    }

    private fun isCurrentScope(key: FileCacheKey): Boolean {
        val activeScope = currentScope()
        val canonicalScope = canonicalKey(key).scope
        return canonicalScope == activeScope || (
            activeScope.accountId == CacheScope.UNKNOWN_ACCOUNT &&
                canonicalScope.serverKey == activeScope.serverKey &&
                reboundAccounts[activeScope.serverKey] == canonicalScope.accountId
            )
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
            pinKeys.clear()
            stateMutex.unlock()
            return gate to active
        }
    }

    private fun cancelFlight(flight: Flight, message: String) {
        flight.result.completeExceptionally(CacheInvalidatedException(message))
        flight.job?.cancel(CancellationException(message))
    }

    private fun canonicalKey(key: FileCacheKey): FileCacheKey {
        val reboundAccount = reboundAccounts[key.scope.serverKey]
        return if (key.scope.accountId == CacheScope.UNKNOWN_ACCOUNT && reboundAccount != null) {
            key.copy(scope = key.scope.copy(accountId = reboundAccount))
        } else {
            key
        }
    }

    private fun addPinLocked(storageKey: String): Long {
        val token = nextPinToken.incrementAndGet()
        pins.getOrPut(storageKey) { mutableSetOf() }.add(token)
        pinKeys[token] = storageKey
        return token
    }

    private fun removePinLocked(token: Long) {
        val storageKey = pinKeys.remove(token) ?: return
        val active = pins[storageKey] ?: return
        active.remove(token)
        if (active.isEmpty()) pins.remove(storageKey)
    }

    private fun movePinsLocked(oldKey: String, newKey: String) {
        if (oldKey == newKey) return
        val activePins = pins.remove(oldKey) ?: return
        pins.getOrPut(newKey) { mutableSetOf() }.addAll(activePins)
        activePins.forEach { pinKeys[it] = newKey }
    }

    private fun moveFlightToKeyLocked(flight: Flight, key: FileCacheKey) {
        val oldKey = flight.storageKey
        val newKey = key.storageKey
        if (oldKey != newKey) {
            flights.remove(oldKey, flight)
            movePinsLocked(oldKey, newKey)
            flight.storageKey = newKey
        }
        flight.key = key
        flights[newKey] = flight
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

    // Coil's two-value DiskCache stores image data as .0 and metadata as .1 beside its journal.
    private fun legacyPreviewFiles(): List<File> =
        filesIn(legacyPreviewsDir).filter { it.name.endsWith(".0") }

    private companion object {
        const val LRU_TOUCH_INTERVAL_MILLIS = 30_000L
    }
}
