import Foundation
import SwiftData

/// Отдельная App Group-БД Upload 2.0. Старый `UploadQueue.sqlite` не открывается
/// этой схемой: при миграции он удаляется вместе с отменёнными V1-артефактами.
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

    @discardableResult
    func createV2(
        id: String = UUID().uuidString,
        source: UploadJobSource,
        sourceFilePath: String,
        fileName: String,
        mimeType: String,
        totalBytes: Int64,
        sha256: String,
        directoryID: String?,
        intent: UploadPostReadyIntent,
        albumID: String? = nil,
        localIdentifier: String? = nil,
        idempotencyKey: String = UUID().uuidString
    ) -> UploadJobSnapshot {
        let context = ModelContext(container)
        let job = UploadJob(
            id: id,
            sourceKind: source.rawValue,
            sourceFilePath: sourceFilePath,
            fileName: fileName,
            mimeType: mimeType,
            directoryID: directoryID,
            stateRaw: UploadJobState.hashing.rawValue,
            bytesSent: 0,
            totalBytes: totalBytes,
            idempotencyKey: idempotencyKey,
            sha256: sha256,
            intentRaw: intent.rawValue,
            albumID: albumID,
            localIdentifier: localIdentifier
        )
        context.insert(job)
        try? context.save()
        return UploadJobSnapshot(job)
    }

    @discardableResult
    func updateState(
        id: String,
        state: UploadJobState,
        lastError: String? = nil,
        returnedFileID: String? = nil,
        retryable: Bool? = nil
    ) -> UploadJobSnapshot? {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: id) else { return nil }
        job.stateRaw = state.rawValue
        job.lastError = lastError
        if let retryable { job.retryable = retryable }
        if let returnedFileID, !returnedFileID.isEmpty {
            job.fileID = returnedFileID
        }
        job.updatedAt = .now
        try? context.save()
        return UploadJobSnapshot(job)
    }

    @discardableResult
    func updateProgress(id: String, bytesSent: Int64, total: Int64? = nil) -> UploadJobSnapshot? {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: id) else { return nil }
        job.bytesSent = max(0, bytesSent)
        if let total, total > 0 { job.totalBytes = total }
        job.updatedAt = .now
        try? context.save()
        return UploadJobSnapshot(job)
    }

    @discardableResult
    func setSession(
        id: String,
        sessionID: String,
        fileID: String,
        partSize: Int64,
        state: UploadJobState = .uploading
    ) -> UploadJobSnapshot? {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: id) else { return nil }
        job.sessionID = sessionID
        job.fileID = fileID
        job.partSize = partSize
        job.stateRaw = state.rawValue
        job.lastError = nil
        job.updatedAt = .now
        try? context.save()
        return UploadJobSnapshot(job)
    }

    @discardableResult
    func setCurrentPart(id: String, part: Int) -> UploadJobSnapshot? {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: id) else { return nil }
        job.currentPart = part
        job.updatedAt = .now
        try? context.save()
        return UploadJobSnapshot(job)
    }

    @discardableResult
    func attachTask(jobID: String, taskIdentifier: Int) -> UploadJobSnapshot? {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: jobID) else { return nil }
        job.sessionTaskIdentifier = taskIdentifier
        job.updatedAt = .now
        try? context.save()
        return UploadJobSnapshot(job)
    }

    @discardableResult
    func clearTask(id: String) -> UploadJobSnapshot? {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: id) else { return nil }
        job.sessionTaskIdentifier = -1
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
        job.sessionTaskIdentifier = -1
        job.lastError = nil
        job.updatedAt = .now
        try? context.save()
    }

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
        return (try? context.fetch(descriptor).first).map(UploadJobSnapshot.init)
    }

    func activeJobs() -> [UploadJobSnapshot] {
        let context = ModelContext(container)
        let states = Set(UploadJobState.allCases.filter { $0.isActive }.map(\.rawValue))
        let descriptor = FetchDescriptor<UploadJob>(sortBy: [SortDescriptor(\.createdAt, order: .forward)])
        return ((try? context.fetch(descriptor)) ?? [])
            .map(UploadJobSnapshot.init)
            .filter { states.contains($0.state.rawValue) }
    }

    func recentJobs(since: Date) -> [UploadJobSnapshot] {
        let context = ModelContext(container)
        let descriptor = FetchDescriptor<UploadJob>(
            predicate: #Predicate { $0.createdAt >= since },
            sortBy: [SortDescriptor(\.createdAt, order: .forward)]
        )
        return ((try? context.fetch(descriptor)) ?? []).map(UploadJobSnapshot.init)
    }

    func retryableJobs(maxRetries: Int) -> [UploadJobSnapshot] {
        let context = ModelContext(container)
        let descriptor = FetchDescriptor<UploadJob>(sortBy: [SortDescriptor(\.createdAt, order: .forward)])
        return ((try? context.fetch(descriptor)) ?? [])
            .map(UploadJobSnapshot.init)
            .filter { ($0.state == .failed && $0.retryable && $0.retries < maxRetries) || $0.state == .uploadedNotAttached }
    }

    func delete(id: String) {
        let context = ModelContext(container)
        guard let job = fetchInContext(context, id: id) else { return }
        context.delete(job)
        try? context.save()
    }

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

    func deleteAll() {
        let context = ModelContext(container)
        try? context.delete(model: UploadJob.self)
        try? context.save()
    }

    private func fetchInContext(_ context: ModelContext, id: String) -> UploadJob? {
        var descriptor = FetchDescriptor<UploadJob>(predicate: #Predicate { $0.id == id })
        descriptor.fetchLimit = 1
        return try? context.fetch(descriptor).first
    }
}

private extension UploadJobState {
    static var allCases: [UploadJobState] {
        [.pending, .preparing, .running, .hashing, .creatingSession, .uploading,
         .completing, .processing, .attaching, .uploadedNotAttached, .completed,
         .failed, .cancelled]
    }
}
