import XCTest
import SwiftData
@testable import BarkCloud

/// Юнит-тесты обслуживающей логики дискового кеша. Записи вставляются напрямую
/// в общий in-memory `ModelContainer`, файлы на диск не пишутся (методы только
/// читают метаданные и удаляют записи — отсутствующие файлы игнорируются).
final class FileCacheServiceTests: XCTestCase {

    private func makeContainer() throws -> ModelContainer {
        try ModelContainer(
            for: CachedFileEntry.self,
            configurations: ModelConfiguration(isStoredInMemoryOnly: true)
        )
    }

    private func makeSettings() -> FileCacheSettings {
        let defaults = UserDefaults(suiteName: "cache-test-\(UUID().uuidString)")!
        return FileCacheSettings(defaults: defaults)
    }

    private func insert(_ container: ModelContainer, key: String, size: Int64, lastAccess: Date) throws {
        let context = ModelContext(container)
        context.insert(CachedFileEntry(
            key: key,
            fileId: key,
            variant: "v",
            sourceURL: nil,
            relativePath: "missing/\(key)",
            sizeBytes: size,
            lastAccessAt: lastAccess,
            createdAt: lastAccess
        ))
        try context.save()
    }

    private func keys(_ container: ModelContainer) throws -> Set<String> {
        let context = ModelContext(container)
        return Set(try context.fetch(FetchDescriptor<CachedFileEntry>()).map(\.key))
    }

    // MARK: - enforceSizeLimit

    func testEnforceSizeLimitEvictsLeastRecentlyUsedFirst() async throws {
        let container = try makeContainer()
        let now = Date()
        try insert(container, key: "old", size: 100, lastAccess: now.addingTimeInterval(-300))
        try insert(container, key: "mid", size: 100, lastAccess: now.addingTimeInterval(-200))
        try insert(container, key: "new", size: 100, lastAccess: now.addingTimeInterval(-100))

        let settings = makeSettings()
        settings.maxCacheBytes = 250  // итого 300 > 250 → выбросить самую старую
        let cache = FileCacheService(modelContainer: container, settings: settings, http: .shared)

        await cache.enforceSizeLimit()

        XCTAssertEqual(try keys(container), ["mid", "new"])
    }

    func testEnforceSizeLimitNoopWhenUnderLimit() async throws {
        let container = try makeContainer()
        try insert(container, key: "a", size: 100, lastAccess: Date())

        let settings = makeSettings()
        settings.maxCacheBytes = 1000
        let cache = FileCacheService(modelContainer: container, settings: settings, http: .shared)

        await cache.enforceSizeLimit()

        XCTAssertEqual(try keys(container), ["a"])
    }

    // MARK: - runStartupSweepIfNeeded

    func testStartupSweepSkippedWhenRecent() async throws {
        let container = try makeContainer()
        let now = Date()
        try insert(container, key: "stale", size: 10, lastAccess: now.addingTimeInterval(-10 * 24 * 3600))

        let settings = makeSettings()
        settings.lastSweepAt = now.addingTimeInterval(-24 * 3600)  // 1 день назад → не пора
        let cache = FileCacheService(modelContainer: container, settings: settings, http: .shared)

        await cache.runStartupSweepIfNeeded()

        XCTAssertEqual(try keys(container), ["stale"], "при недавнем sweep устаревшее не трогаем")
        XCTAssertEqual(
            settings.lastSweepAt!.timeIntervalSince1970,
            now.addingTimeInterval(-24 * 3600).timeIntervalSince1970,
            accuracy: 1,
            "lastSweepAt не должен обновляться, если sweep пропущен"
        )
    }

    func testStartupSweepRunsWhenDue() async throws {
        let container = try makeContainer()
        let now = Date()
        try insert(container, key: "stale", size: 10, lastAccess: now.addingTimeInterval(-10 * 24 * 3600))
        try insert(container, key: "fresh", size: 10, lastAccess: now)

        let settings = makeSettings()
        settings.lastSweepAt = now.addingTimeInterval(-10 * 24 * 3600)  // 10 дней назад → пора
        let cache = FileCacheService(modelContainer: container, settings: settings, http: .shared)

        await cache.runStartupSweepIfNeeded()

        XCTAssertEqual(try keys(container), ["fresh"], "устаревшее (>7 дней) удалено, свежее осталось")
        XCTAssertEqual(
            settings.lastSweepAt!.timeIntervalSince1970,
            now.timeIntervalSince1970,
            accuracy: 5,
            "lastSweepAt обновлён до текущего момента"
        )
    }

    func testStartupSweepRunsWhenNeverSwept() async throws {
        let container = try makeContainer()
        let now = Date()
        try insert(container, key: "stale", size: 10, lastAccess: now.addingTimeInterval(-10 * 24 * 3600))

        let settings = makeSettings()  // lastSweepAt == nil
        let cache = FileCacheService(modelContainer: container, settings: settings, http: .shared)

        await cache.runStartupSweepIfNeeded()

        XCTAssertTrue(try keys(container).isEmpty, "первый запуск чистит устаревшее")
        XCTAssertNotNil(settings.lastSweepAt, "lastSweepAt проставлен")
    }
}
