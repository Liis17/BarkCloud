import Foundation
import SwiftData

/// Persist-очередь UploadJob поверх SwiftData. Контейнер хранится в App Group,
/// чтобы был доступен и main app, и Share Extension (oба используют один и тот
/// же `UploadConstants.uploadQueueDatabaseURL`). При сбое открытия откатываемся
/// на in-memory.
actor UploadQueueStore {
    static let shared = UploadQueueStore()

    private let container: ModelContainer

    private init() {
        if let url = UploadConstants.uploadQueueDatabaseURL,
           let c = try? ModelContainer(
               for: UploadJob.self,
               configurations: ModelConfiguration(url: url)
           ) {
            container = c
        } else {
            container = try! ModelContainer(
                for: UploadJob.self,
                configurations: ModelConfiguration(isStoredInMemoryOnly: true)
            )
        }
    }

    // MARK: - Create

    @discardableResult
    func create(
        id: String = UUID().uuidString,
        sourceKind: UploadJobSource,
        sourceFilePath: String,
        multipartBodyPath: String,
        fileName: String,
        mimeType: String,
        directoryID: String?,
        uploadURL: String,
        preparedFileID: String,
        totalBytes: Int64
    ) -> UploadJobSnapshot {
        let context = ModelContext(container)
        let job = UploadJob(
            id: id,
            sourceKind: sourceKind.rawValue,
            sourceFilePath: sourceFilePath,
            multipartBodyPath: multipartBodyPath,
            fileName: fileName,
            mimeType: mimeType,
            directoryID: directoryID,
            uploadURL: uploadURL,
            preparedFileID: preparedFileID,
            stateRaw: UploadJobState.pending.rawValue,
            bytesSent: 0,
            totalBytes: totalBytes,
            sessionTaskIdentifier: -1,
            retries: 0,
            lastError: nil,
            createdAt: .now,
            updatedAt: .now
        )
        context.insert(job)
        try? context.save()
        return UploadJobSnapshot(job)
    }

    // MARK: - Update

    /// Привязать taskIdentifier из URLSession к существующему job и перевести
    /// в `running`. Возвращает `nil`, если job не найден (например, удалён).
    @discardableResult
    func attachTask(jobID: String, taskIdentifier: Int) -> UploadJobSnapshot? {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: jobID) else { return nil }
        job.sessionTaskIdentifier = taskIdentifier
        job.stateRaw = UploadJobState.running.rawValue
        job.updatedAt = .now
        try? context.save()
        return UploadJobSnapshot(job)
    }

    @discardableResult
    func updateProgress(id: String, bytesSent: Int64, total: Int64) -> UploadJobSnapshot? {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: id) else { return nil }
        job.bytesSent = bytesSent
        if total > 0 { job.totalBytes = total }
        job.updatedAt = .now
        try? context.save()
        return UploadJobSnapshot(job)
    }

    @discardableResult
    func updateState(
        id: String,
        state: UploadJobState,
        lastError: String? = nil,
        returnedFileID: String? = nil
    ) -> UploadJobSnapshot? {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: id) else { return nil }
        job.stateRaw = state.rawValue
        job.lastError = lastError
        if let returnedFileID, !returnedFileID.isEmpty {
            job.preparedFileID = returnedFileID
        }
        job.updatedAt = .now
        try? context.save()
        return UploadJobSnapshot(job)
    }

    func incrementRetries(id: String) {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: id) else { return }
        job.retries += 1
        job.updatedAt = .now
        try? context.save()
    }

    func resetForRetry(id: String) {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: id) else { return }
        job.stateRaw = UploadJobState.pending.rawValue
        job.bytesSent = 0
        job.sessionTaskIdentifier = -1
        job.lastError = nil
        job.updatedAt = .now
        try? context.save()
    }

    // MARK: - Fetch

    func fetch(id: String) -> UploadJobSnapshot? {
        let context = ModelContext(container)
        return fetchInContext(context, id: id).map(UploadJobSnapshot.init)
    }

    func fetch(byTaskIdentifier taskID: Int) -> UploadJobSnapshot? {
        let context = ModelContext(container)
        var descriptor = FetchDescriptor<UploadJob>(
            predicate: #Predicate { $0.sessionTaskIdentifier == taskID }
        )
        descriptor.fetchLimit = 1
        return (try? context.fetch(descriptor).first).map { UploadJobSnapshot($0) }
    }

    /// Все активные (pending|preparing|running) — для re-attaching при старте.
    func activeJobs() -> [UploadJobSnapshot] {
        let context = ModelContext(container)
        let pending = UploadJobState.pending.rawValue
        let preparing = UploadJobState.preparing.rawValue
        let running = UploadJobState.running.rawValue
        let descriptor = FetchDescriptor<UploadJob>(
            predicate: #Predicate { $0.stateRaw == pending || $0.stateRaw == preparing || $0.stateRaw == running },
            sortBy: [SortDescriptor(\.createdAt, order: .forward)]
        )
        return ((try? context.fetch(descriptor)) ?? []).map(UploadJobSnapshot.init)
    }

    /// Все jobs созданные после указанной даты — используется Live Activity
    /// контроллером, чтобы посчитать совокупный прогресс «N из M» по текущей
    /// сессии загрузки (без учёта старых завершённых).
    func recentJobs(since: Date) -> [UploadJobSnapshot] {
        let context = ModelContext(container)
        let descriptor = FetchDescriptor<UploadJob>(
            predicate: #Predicate { $0.createdAt >= since },
            sortBy: [SortDescriptor(\.createdAt, order: .forward)]
        )
        return ((try? context.fetch(descriptor)) ?? []).map(UploadJobSnapshot.init)
    }

    /// Все failed job для retry-логики (BGTask).
    func failedJobs(maxRetries: Int) -> [UploadJobSnapshot] {
        let context = ModelContext(container)
        let failed = UploadJobState.failed.rawValue
        let descriptor = FetchDescriptor<UploadJob>(
            predicate: #Predicate { $0.stateRaw == failed && $0.retries < maxRetries },
            sortBy: [SortDescriptor(\.createdAt, order: .forward)]
        )
        return ((try? context.fetch(descriptor)) ?? []).map(UploadJobSnapshot.init)
    }

    // MARK: - Delete

    func delete(id: String) {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: id) else { return }
        context.delete(job)
        try? context.save()
    }

    /// Удалить все завершённые job старше определённого возраста — вызывается при
    /// старте main app для cleanup'а старых записей.
    func purgeCompleted(olderThan: Date) {
        let context = ModelContext(container)
        let completed = UploadJobState.completed.rawValue
        let descriptor = FetchDescriptor<UploadJob>(
            predicate: #Predicate { $0.stateRaw == completed && $0.updatedAt < olderThan }
        )
        guard let jobs = try? context.fetch(descriptor) else { return }
        for job in jobs { context.delete(job) }
        try? context.save()
    }

    /// Полная очистка очереди — при выходе из аккаунта / полном сбросе устройства.
    func deleteAll() {
        let context = ModelContext(container)
        try? context.delete(model: UploadJob.self)
        try? context.save()
    }

    // MARK: - Helpers

    private func fetchInContext(_ context: ModelContext, id: String) -> UploadJob? {
        var descriptor = FetchDescriptor<UploadJob>(predicate: #Predicate { $0.id == id })
        descriptor.fetchLimit = 1
        return try? context.fetch(descriptor).first
    }
}
