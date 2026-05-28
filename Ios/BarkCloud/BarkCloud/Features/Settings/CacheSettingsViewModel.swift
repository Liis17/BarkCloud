import Foundation
import Observation

/// Состояние и действия раздела «Кеш» в Настройках. Все операции делегируются
/// актору `FileCacheService`; лимит хранится в `FileCacheSettings`.
@MainActor
@Observable
final class CacheSettingsViewModel {
    struct UiState {
        var sizeBytes: Int64 = 0
        var entryCount: Int = 0
        var limitBytes: Int64 = FileCacheSettings.defaultMaxBytes
        var isWorking = false
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
        state.isWorking = true
        await refreshStats()
        state.isWorking = false
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
        await refreshStats()
        state.isWorking = false
    }

    private func refreshStats() async {
        state.sizeBytes = await cache.totalSize()
        state.entryCount = await cache.entryCount()
    }
}
