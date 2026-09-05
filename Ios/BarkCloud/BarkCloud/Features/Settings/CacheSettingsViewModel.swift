import Foundation
import Observation

/// Состояние и действия раздела «Кеш» в Настройках. Основные операции делегируются
/// актору `FileCacheService`; полная очистка также удаляет сиротские tmp/upload-файлы.
@MainActor
@Observable
final class CacheSettingsViewModel {
    struct UiState {
        var sizeBytes: Int64 = 0
        var entryCount: Int = 0
        var limitBytes: Int64 = FileCacheSettings.defaultMaxBytes
        /// Порог автоочистки по возрасту; `nil` — «Никогда».
        var staleMaxAge: TimeInterval? = FileCacheSettings.defaultStaleMaxAge
        var deviceFreeBytes: Int64 = 0
        var deviceTotalBytes: Int64 = 0
        var isWorking = false

        /// Занято на устройстве «другими» данными (без нашего кеша).
        var deviceOtherBytes: Int64 {
            max(0, deviceTotalBytes - deviceFreeBytes - sizeBytes)
        }
    }

    var state = UiState()

    private let cache: FileCacheService
    private let settings: FileCacheSettings

    init(cache: FileCacheService, settings: FileCacheSettings) {
        self.cache = cache
        self.settings = settings
    }

    func load() async {
        state.limitBytes = settings.maxCacheBytes
        state.staleMaxAge = settings.staleMaxAge
        let device = Self.deviceStorage()
        state.deviceFreeBytes = device.free
        state.deviceTotalBytes = device.total
        state.isWorking = true
        await refreshStats()
        state.isWorking = false
    }

    func setStaleMaxAge(_ value: TimeInterval?) {
        settings.staleMaxAge = value
        state.staleMaxAge = value
    }

    /// Свободная/полная ёмкость тома устройства (для прогресс-бара хранилища).
    private static func deviceStorage() -> (free: Int64, total: Int64) {
        let keys: Set<URLResourceKey> = [
            .volumeAvailableCapacityForImportantUsageKey,
            .volumeTotalCapacityKey
        ]
        guard let values = try? URL.homeDirectory.resourceValues(forKeys: keys),
              let total = values.volumeTotalCapacity,
              let free = values.volumeAvailableCapacityForImportantUsage else {
            return (0, 0)
        }
        return (free, Int64(total))
    }

    func setLimit(_ bytes: Int64) async {
        settings.maxCacheBytes = bytes
        state.limitBytes = bytes
        state.isWorking = true
        await cache.enforceSizeLimit()
        await refreshStats()
        state.isWorking = false
    }

    func clearStale() async {
        state.isWorking = true
        await cache.evictStale()
        await refreshStats()
        state.isWorking = false
    }

    func clearAll() async {
        state.isWorking = true
        await cache.clearAll()
        let activeJobs = await UploadQueueStore.shared.activeJobs()
        let retryableJobs = await UploadQueueStore.shared.failedJobs(
            maxRetries: UploadConstants.maxUploadRetries
        )
        let referencedPaths = Set((activeJobs + retryableJobs).flatMap {
            [$0.sourceFilePath, $0.multipartBodyPath]
        })
        UploadConstants.purgeOrphanedStaging(
            referencedPaths: referencedPaths,
            olderThan: 3600
        )
        TemporaryFileCleanup.purgeStale(olderThan: 24 * 3600)
        ShareInbox.purgeStale()
        await refreshStats()
        state.isWorking = false
    }

    private func refreshStats() async {
        state.sizeBytes = await cache.totalSize()
        state.entryCount = await cache.entryCount()
        let device = Self.deviceStorage()
        state.deviceFreeBytes = device.free
        state.deviceTotalBytes = device.total
    }
}
