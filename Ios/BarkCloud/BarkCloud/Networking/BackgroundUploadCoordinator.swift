import Foundation
import CryptoKit
import GRPCCore
import UniformTypeIdentifiers
import BarkCloudKit

/// Upload 2.0 authenticates the data plane with the per-session token, so an
/// access token is not put into part requests.
typealias UploadPostReadyHandler = @MainActor @Sendable (UploadJobSnapshot, Bool) async -> UploadPostReadyResult

private struct UploadPartResult: Sendable {
    let statusCode: Int
    let body: Data
    let error: String?
}

private struct UploadSessionTerminalError: LocalizedError, Sendable {
    let status: FileUploadSessionStatus
    let message: String

    var errorDescription: String? { message }
}

/// Координатор Upload 2.0. URLSession передаёт только один ограниченный part-файл
/// за раз; исходный объект никогда не собирается в `Data` и не помещается в
/// multipart-body. Внутри процесса токен хранится в памяти, а после рестарта
/// всегда запрашивается через `ResumeUploadSession`.
final class BackgroundUploadCoordinator: NSObject, URLSessionDelegate, URLSessionTaskDelegate, URLSessionDataDelegate, @unchecked Sendable {
    static let shared = BackgroundUploadCoordinator(queueStore: .shared)

    private let queueStore: UploadQueueStore
    private let lock = NSLock()
    private var responseBuffers: [Int: Data] = [:]
    private var partWaiters: [Int: CheckedContinuation<UploadPartResult, Never>] = [:]
    private var processingIDs = Set<String>()
    private var sessionTokens: [String: String] = [:]
    private var backgroundCompletionHandler: (() -> Void)?
    private var transfer: FileTransferService?
    private var postReadyHandler: UploadPostReadyHandler?

    private var progressListeners: [@MainActor @Sendable (UploadJobSnapshot) -> Void] = []
    private var completionListeners: [@MainActor @Sendable (UploadJobSnapshot) -> Void] = []
    private var failureListeners: [@MainActor @Sendable (UploadJobSnapshot) -> Void] = []

    /// Системный background identifier сохраняется для совместимости с уже
    /// установленными версиями приложения. Миграция сначала отменяет старые V1
    /// задачи, после чего callbacks неизвестных task id игнорируются.
    private(set) lazy var session: URLSession = {
        let config = URLSessionConfiguration.background(withIdentifier: UploadConstants.uploadSessionIdentifier)
        config.sharedContainerIdentifier = UploadConstants.appGroupID
        config.isDiscretionary = false
        config.sessionSendsLaunchEvents = true
        config.allowsCellularAccess = true
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 2 * 60 * 60
        return URLSession(configuration: config, delegate: self, delegateQueue: nil)
    }()

    init(queueStore: UploadQueueStore) {
        self.queueStore = queueStore
        super.init()
    }

    func configure(transfer: FileTransferService, postReady: UploadPostReadyHandler? = nil) {
        lock.lock()
        self.transfer = transfer
        self.postReadyHandler = postReady
        lock.unlock()
        _ = session
    }

    func addObserver(
        progress: (@MainActor @Sendable (UploadJobSnapshot) -> Void)? = nil,
        completion: (@MainActor @Sendable (UploadJobSnapshot) -> Void)? = nil,
        failure: (@MainActor @Sendable (UploadJobSnapshot) -> Void)? = nil
    ) {
        lock.lock()
        if let progress { progressListeners.append(progress) }
        if let completion { completionListeners.append(completion) }
        if let failure { failureListeners.append(failure) }
        lock.unlock()
    }

    func setBackgroundCompletionHandler(_ handler: @escaping () -> Void) {
        lock.lock()
        backgroundCompletionHandler = handler
        lock.unlock()
        _ = session
    }

    // MARK: - Migration / lifecycle

    func migrateLegacyQueueIfNeeded() async {
        guard !UploadConstants.uploadMigrationCompleted else { return }
        await cancelAll()
        await queueStore.deleteAll()
        UploadConstants.removeLegacyUploadStoreAndStaging()
        UploadConstants.markUploadMigrationCompleted()
    }

    func attachAndResubmitOrphans() async {
        _ = session
        await queueStore.purgeCompleted(olderThan: Date().addingTimeInterval(-7 * 24 * 3600))
        let live = await currentTaskIdentifiers()
        let jobs = await queueStore.activeJobs()
        // A background URLSession task can outlive this process. Reserve its
        // file slot before submitting any queued work so a relaunch cannot
        // start four additional files alongside the live tasks.
        let liveJobs = jobs.filter {
            $0.sessionTaskIdentifier >= 0 && live.contains($0.sessionTaskIdentifier)
        }
        withLock { processingIDs.formUnion(liveJobs.map(\.id)) }
        for job in jobs {
            if job.sessionTaskIdentifier >= 0,
               live.contains(job.sessionTaskIdentifier) {
                continue
            }
            await submit(jobID: job.id)
        }
        await UploadLiveActivityController.shared.notifyChanged()
    }

    func cancelAll() async {
        let tasks: [URLSessionTask] = await withCheckedContinuation { continuation in
            session.getAllTasks { continuation.resume(returning: $0) }
        }
        for task in tasks { task.cancel() }
        let waiters: [CheckedContinuation<UploadPartResult, Never>] = withLock {
            responseBuffers.removeAll()
            sessionTokens.removeAll()
            let result = Array(partWaiters.values)
            partWaiters.removeAll()
            return result
        }
        for waiter in waiters {
            waiter.resume(returning: UploadPartResult(statusCode: 0, body: Data(), error: "Cancelled"))
        }
        for job in await queueStore.activeJobs() {
            if let transfer = configuredTransfer(), !job.sessionID.isEmpty {
                _ = try? await transfer.cancelUploadSession(sessionID: job.sessionID)
            }
            let snapshot = await queueStore.updateState(id: job.id, state: .cancelled, lastError: "Cancelled")
                ?? job
            removeArtifacts(for: snapshot)
            await notifyFailure(snapshot)
        }
        await UploadLiveActivityController.shared.notifyChanged()
    }

    func cancelActiveJobs(source: UploadJobSource) async {
        let jobs = await queueStore.activeJobs().filter { $0.source == source }
        guard !jobs.isEmpty else { return }
        let ids = Set(jobs.map(\.sessionTaskIdentifier))
        let tasks: [URLSessionTask] = await withCheckedContinuation { continuation in
            session.getAllTasks { continuation.resume(returning: $0) }
        }
        for task in tasks where ids.contains(task.taskIdentifier) { task.cancel() }
        for job in jobs {
            if let transfer = configuredTransfer(), !job.sessionID.isEmpty {
                _ = try? await transfer.cancelUploadSession(sessionID: job.sessionID)
            }
            let snapshot = await queueStore.updateState(id: job.id, state: .cancelled, lastError: "Cancelled by user")
                ?? job
            removeArtifacts(for: snapshot)
            await notifyFailure(snapshot)
        }
        await UploadLiveActivityController.shared.notifyChanged()
    }

    func blockingActiveJobs(from active: [UploadJobSnapshot]) async -> [UploadJobSnapshot] {
        active.filter(\.state.isActive)
    }

    // MARK: - Scheduling

    func submit(jobID: String) async {
        guard let job = await queueStore.fetch(id: jobID), !job.state.isTerminal else { return }
        let accepted = withLock {
            guard !processingIDs.contains(jobID), processingIDs.count < 4 else { return false }
            processingIDs.insert(jobID)
            return true
        }
        guard accepted else { return }
        Task { [weak self] in
            guard let self else { return }
            await self.process(jobID: jobID)
            await self.finishProcessing(jobID: jobID)
        }
    }

    /// Used by a BGProcessingTask. It keeps that task alive until the queue
    /// has either submitted its URLSession part task or reached a state that no
    /// longer needs this process (processing/attaching/terminal).
    @discardableResult
    func submitAndWaitForBackgroundStart(jobID: String) async -> Bool {
        await submit(jobID: jobID)
        let deadline = Date().addingTimeInterval(30)
        while !Task.isCancelled, Date() < deadline {
            guard let job = await queueStore.fetch(id: jobID) else { return false }
            if job.sessionTaskIdentifier >= 0 ||
                job.state == .processing || job.state == .attaching || job.state.isTerminal {
                return true
            }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        return false
    }

    func retryAttachment(jobID: String) async {
        guard let job = await queueStore.fetch(id: jobID), job.state == .uploadedNotAttached else { return }
        await submit(jobID: jobID)
    }

    func waitForReady(jobID: String) async throws -> String {
        while !Task.isCancelled {
            guard let job = await queueStore.fetch(id: jobID) else { throw FileTransferError.badUploadResponse }
            switch job.state {
            case .completed:
                guard !job.fileID.isEmpty else { throw FileTransferError.badUploadResponse }
                return job.fileID
            case .failed, .cancelled:
                throw UploadSessionTerminalError(status: .failed, message: job.lastError ?? "Upload failed")
            case .uploadedNotAttached:
                // The bytes are already ready. Do not turn a post-ready error
                // into a hot upload retry loop; the UI/BG retry path invokes
                // AttachFile explicitly and only that operation is repeated.
                throw UploadSessionTerminalError(
                    status: .ready,
                    message: job.lastError ?? "File uploaded but could not be attached"
                )
            default:
                break
            }
            try? await Task.sleep(nanoseconds: 500_000_000)
        }
        throw CancellationError()
    }

    private func finishProcessing(jobID: String) async {
        _ = withLock { processingIDs.remove(jobID) }
        await scheduleQueuedJobs()
        await UploadLiveActivityController.shared.notifyChanged()
    }

    private func scheduleQueuedJobs() async {
        let jobs = await queueStore.activeJobs()
        for job in jobs {
            // `uploadedNotAttached` is deliberately retained in the active
            // queue for UI/backup bookkeeping, but it must never be picked up
            // by the ordinary scheduler. Its only retry path is explicit
            // AttachFile retry (or the BG retry task).
            guard job.state != .uploadedNotAttached else { continue }
            let (hasCapacity, alreadyProcessing) = withLock {
                (processingIDs.count < 4, processingIDs.contains(job.id))
            }
            guard hasCapacity, !alreadyProcessing else { continue }
            await submit(jobID: job.id)
        }
    }

    // MARK: - Upload pipeline

    private func process(jobID: String) async {
        guard let transfer = configuredTransfer(), let initial = await queueStore.fetch(id: jobID) else {
            await fail(jobID: jobID, message: "Upload service is not configured", retryable: true)
            return
        }

        if initial.state == .uploadedNotAttached {
            await attachReady(initial, retry: true)
            return
        }
        guard FileManager.default.fileExists(atPath: initial.sourceFilePath) else {
            await fail(jobID: jobID, message: "Source file is missing", retryable: false)
            return
        }

        do {
            var info = try await obtainSession(for: initial, transfer: transfer)
            switch info.status {
            case .failed, .cancelled, .expired:
                throw UploadSessionTerminalError(
                    status: info.status,
                    message: info.errorMessage ?? "Upload session is no longer available"
                )
            case .ready:
                await attachReady(
                    await queueStore.fetch(id: jobID) ?? initial,
                    retry: false
                )
                return
            case .processing:
                try await waitForProcessing(jobID: jobID, transfer: transfer)
                return
            case .uploading, .unspecified:
                if info.uploadToken == nil {
                    info = try await resumeSession(for: initial, transfer: transfer)
                }
                switch info.status {
                case .ready:
                    await attachReady(await queueStore.fetch(id: jobID) ?? initial, retry: false)
                    return
                case .processing:
                    try await waitForProcessing(jobID: jobID, transfer: transfer)
                    return
                case .failed, .cancelled, .expired:
                    throw UploadSessionTerminalError(
                        status: info.status,
                        message: info.errorMessage ?? "Upload session is no longer available"
                    )
                case .uploading, .unspecified:
                    break
                }
                try await uploadMissingParts(jobID: jobID, info: info, transfer: transfer)
                _ = await queueStore.updateState(id: jobID, state: .completing)
                let completed = try await completeWithRecovery(jobID: jobID, transfer: transfer)
                if completed.status == .ready {
                    await attachReady(await queueStore.fetch(id: jobID) ?? initial, retry: false)
                } else {
                    try await waitForProcessing(jobID: jobID, transfer: transfer)
                }
            }
        } catch is CancellationError {
            await fail(jobID: jobID, message: "Cancelled", retryable: false)
        } catch let error as UploadSessionTerminalError {
            await fail(jobID: jobID, message: error.message, retryable: false)
        } catch {
            await fail(jobID: jobID, message: error.localizedDescription, retryable: true)
        }
    }

    private func obtainSession(
        for job: UploadJobSnapshot,
        transfer: FileTransferService
    ) async throws -> FileUploadSession {
        if job.sessionID.isEmpty {
            _ = await queueStore.updateState(id: job.id, state: .creatingSession)
            let created = try await transfer.createUploadSession(
                idempotencyKey: job.idempotencyKey,
                fileName: job.fileName,
                fileSize: job.totalBytes,
                contentType: job.mimeType,
                sha256: job.sha256
            )
            return try await storeSession(created, jobID: job.id)
        }
        if job.state == .processing || job.state == .attaching {
            let current = try await transfer.getUploadSession(sessionID: job.sessionID)
            return try await storeSession(current, jobID: job.id)
        }
        return try await resumeSession(for: job, transfer: transfer)
    }

    private func resumeSession(
        for job: UploadJobSnapshot,
        transfer: FileTransferService
    ) async throws -> FileUploadSession {
        let resumed = try await transfer.resumeUploadSession(sessionID: job.sessionID)
        return try await storeSession(resumed, jobID: job.id)
    }

    private func storeSession(_ info: FileUploadSession, jobID: String) async throws -> FileUploadSession {
        guard !info.sessionID.isEmpty, info.partSize > 0 else {
            throw FileTransferError.badUploadResponse
        }
        let state: UploadJobState
        switch info.status {
        case .processing: state = .processing
        case .ready: state = .attaching
        default: state = .uploading
        }
        _ = await queueStore.setSession(
            id: jobID,
            sessionID: info.sessionID,
            fileID: info.fileID,
            partSize: info.partSize,
            state: state
        )
        if let token = info.uploadToken {
            withLock { sessionTokens[jobID] = token }
        }
        return info
    }

    private func uploadMissingParts(
        jobID: String,
        info: FileUploadSession,
        transfer: FileTransferService
    ) async throws {
        guard let job = await queueStore.fetch(id: jobID) else { throw FileTransferError.badUploadResponse }
        guard info.fileSize == 0 || info.fileSize == job.totalBytes else {
            throw UploadSessionTerminalError(status: .failed, message: "File size changed")
        }
        let total = job.totalBytes
        let partSize = info.partSize
        let partCount = total == 0 ? 1 : Int((total + partSize - 1) / partSize)
        let uploaded = Dictionary(uniqueKeysWithValues: info.uploadedParts.map { ($0.partNumber, $0) })
        for partNumber in 1...partCount {
            try Task.checkCancellation()
            let start = Int64(partNumber - 1) * partSize
            let size = total == 0 ? 0 : min(partSize, total - start)
            if let part = uploaded[partNumber], part.hasETag, part.size == size {
                _ = await queueStore.updateProgress(id: jobID, bytesSent: start + size, total: total)
                continue
            }
            _ = await queueStore.setCurrentPart(id: jobID, part: partNumber)
            _ = await queueStore.updateState(id: jobID, state: .uploading)
            let partFile = try materializePart(sourcePath: job.sourceFilePath, jobID: jobID, part: partNumber, start: start, size: size)
            defer { try? FileManager.default.removeItem(at: partFile) }
            let result = try await uploadPart(
                jobID: jobID,
                sessionID: info.sessionID,
                partNumber: partNumber,
                rangeStart: start,
                rangeEnd: start + max(0, size - 1),
                totalSize: total,
                fileURL: partFile,
                transfer: transfer
            )
            guard result.statusCode == 200 || result.statusCode == 201,
                  Self.validPartAck(result.body, partNumber: partNumber, size: size) else {
                throw FileTransferError.badUploadResponse
            }
            _ = await queueStore.updateProgress(id: jobID, bytesSent: start + size, total: total)
        }
    }

    private func completeWithRecovery(
        jobID: String,
        transfer: FileTransferService
    ) async throws -> FileUploadSession {
        guard let job = await queueStore.fetch(id: jobID), !job.sessionID.isEmpty else {
            throw FileTransferError.badUploadResponse
        }
        do {
            return try await transfer.completeUploadSession(sessionID: job.sessionID)
        } catch let completeError {
            // Complete may have reached S3 while its gRPC response was lost.
            // Reconcile once before retrying: a ready/processing session is a
            // successful completion, while an uploading session supplies the
            // authoritative missing-part list through Resume.
            let current: FileUploadSession
            do {
                current = try await transfer.getUploadSession(sessionID: job.sessionID)
            } catch {
                throw completeError
            }
            switch current.status {
            case .ready, .processing:
                return current
            case .uploading, .unspecified:
                let resumed = try await resumeSession(for: job, transfer: transfer)
                switch resumed.status {
                case .ready, .processing:
                    return resumed
                case .failed, .cancelled, .expired:
                    throw UploadSessionTerminalError(
                        status: resumed.status,
                        message: resumed.errorMessage ?? completeError.localizedDescription
                    )
                case .uploading, .unspecified:
                    break
                }
                try await uploadMissingParts(jobID: jobID, info: resumed, transfer: transfer)
                guard let refreshed = await queueStore.fetch(id: jobID) else {
                    throw FileTransferError.badUploadResponse
                }
                return try await transfer.completeUploadSession(sessionID: refreshed.sessionID)
            case .failed, .cancelled, .expired:
                throw UploadSessionTerminalError(
                    status: current.status,
                    message: current.errorMessage ?? completeError.localizedDescription
                )
            }
        }
    }

    private func waitForProcessing(jobID: String, transfer: FileTransferService) async throws {
        guard let job = await queueStore.fetch(id: jobID), !job.sessionID.isEmpty else {
            throw FileTransferError.badUploadResponse
        }
        while !Task.isCancelled {
            let info = try await transfer.getUploadSession(sessionID: job.sessionID)
            switch info.status {
            case .ready:
                await attachReady(await queueStore.fetch(id: jobID) ?? job, retry: false)
                return
            case .processing:
                _ = await queueStore.updateState(id: jobID, state: .processing)
                _ = await queueStore.updateProgress(id: jobID, bytesSent: job.totalBytes, total: job.totalBytes)
            case .failed, .cancelled, .expired:
                throw UploadSessionTerminalError(
                    status: info.status,
                    message: info.errorMessage ?? "File processing failed"
                )
            default:
                break
            }
            try await Task.sleep(nanoseconds: 2_000_000_000)
        }
        throw CancellationError()
    }

    private func attachReady(_ job: UploadJobSnapshot, retry: Bool) async {
        guard !job.fileID.isEmpty else {
            await fail(jobID: job.id, message: "Ready session has no file id", retryable: false)
            return
        }
        guard let handler = configuredPostReadyHandler() else {
            if job.intent == .none {
                await complete(job)
            } else {
                await markUploadedNotAttached(job, message: "Post-ready handler is unavailable")
            }
            return
        }
        _ = await queueStore.updateState(id: job.id, state: .attaching)
        _ = await queueStore.updateProgress(id: job.id, bytesSent: job.totalBytes, total: job.totalBytes)
        let current = await queueStore.fetch(id: job.id) ?? job
        let result = await handler(current, retry)
        switch result {
        case .completed:
            await complete(current)
        case .failed(let message):
            await markUploadedNotAttached(current, message: message)
        }
    }

    private func complete(_ job: UploadJobSnapshot) async {
        let snapshot = await queueStore.updateState(id: job.id, state: .completed, lastError: nil, returnedFileID: job.fileID)
            ?? job
        removeArtifacts(for: snapshot)
        clearToken(jobID: job.id)
        await notifyCompletion(snapshot)
    }

    private func markUploadedNotAttached(_ job: UploadJobSnapshot, message: String) async {
        let snapshot = await queueStore.updateState(id: job.id, state: .uploadedNotAttached, lastError: message)
            ?? job
        clearToken(jobID: job.id)
        await notifyFailure(snapshot)
    }

    private func fail(jobID: String, message: String, retryable: Bool) async {
        guard let current = await queueStore.fetch(id: jobID) else { return }
        // Cancellation/terminal callbacks can arrive after the URLSession task
        // has already been marked cancelled. Never resurrect such a record as
        // a retryable failure.
        guard current.state.isActive else { return }
        let snapshot = await queueStore.updateState(
            id: jobID,
            state: .failed,
            lastError: message,
            retryable: retryable
        ) ?? current
        if !retryable || snapshot.retries >= UploadConstants.maxUploadRetries {
            removeArtifacts(for: snapshot)
        }
        await notifyFailure(snapshot)
    }

    // MARK: - HTTP part transport

    private func uploadPart(
        jobID: String,
        sessionID: String,
        partNumber: Int,
        rangeStart: Int64,
        rangeEnd: Int64,
        totalSize: Int64,
        fileURL: URL,
        transfer: FileTransferService
    ) async throws -> UploadPartResult {
        guard let token = token(for: jobID) else { throw FileTransferError.badUploadResponse }
        let url = try transfer.uploadSessionPartURL(sessionID: sessionID, partNumber: partNumber)
        var request = URLRequest(url: url)
        request.httpMethod = "PUT"
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.setValue("bytes \(rangeStart)-\(rangeEnd)/\(totalSize)", forHTTPHeaderField: "Content-Range")
        request.setValue(token, forHTTPHeaderField: "X-Upload-Token")
        request.setValue(String(max(0, rangeEnd - rangeStart + 1)), forHTTPHeaderField: "Content-Length")

        let task = session.uploadTask(with: request, fromFile: fileURL)
        let taskID = task.taskIdentifier
        _ = await queueStore.attachTask(jobID: jobID, taskIdentifier: taskID)
        let result = await withCheckedContinuation { (continuation: CheckedContinuation<UploadPartResult, Never>) in
            lock.lock()
            responseBuffers[taskID] = Data()
            partWaiters[taskID] = continuation
            lock.unlock()
            task.resume()
        }
        _ = await queueStore.clearTask(id: jobID)
        return result
    }

    private func materializePart(
        sourcePath: String,
        jobID: String,
        part: Int,
        start: Int64,
        size: Int64
    ) throws -> URL {
        guard let staging = UploadConstants.stagingDirectory else { throw FileTransferError.badURL }
        let destination = staging.appendingPathComponent("v2-\(jobID)-part-\(part).bin")
        try? FileManager.default.removeItem(at: destination)
        FileManager.default.createFile(atPath: destination.path, contents: nil)
        let input = try FileHandle(forReadingFrom: URL(fileURLWithPath: sourcePath))
        defer { try? input.close() }
        try input.seek(toOffset: UInt64(start))
        let output = try FileHandle(forWritingTo: destination)
        defer { try? output.close() }
        do {
            var remaining = size
            while remaining > 0 {
                let length = Int(min(Int64(4 * 1024 * 1024), remaining))
                guard let chunk = try input.read(upToCount: length), !chunk.isEmpty else {
                    throw FileTransferError.badUploadResponse
                }
                try output.write(contentsOf: chunk)
                remaining -= Int64(chunk.count)
            }
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw error
        }
        return destination
    }

    // MARK: - URLSession delegates

    nonisolated func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              GrpcEndpoint.allowSelfSigned,
              challenge.protectionSpace.host == GrpcEndpoint.filesHost,
              let trust = challenge.protectionSpace.serverTrust else {
            completionHandler(.performDefaultHandling, nil)
            return
        }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }

    nonisolated func urlSessionDidFinishEvents(forBackgroundURLSession session: URLSession) {
        lock.lock()
        let handler = backgroundCompletionHandler
        backgroundCompletionHandler = nil
        lock.unlock()
        if let handler { DispatchQueue.main.async { handler() } }
    }

    nonisolated func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didSendBodyData bytesSent: Int64,
        totalBytesSent: Int64,
        totalBytesExpectedToSend: Int64
    ) {
        let taskID = task.taskIdentifier
        Task { [weak self] in
            guard let self, let job = await self.queueStore.fetch(byTaskIdentifier: taskID) else { return }
            let start = Int64(max(0, job.currentPart - 1)) * max(1, job.partSize)
            _ = await self.queueStore.updateProgress(
                id: job.id,
                bytesSent: min(job.totalBytes, start + totalBytesSent),
                total: job.totalBytes
            )
            if let snapshot = await self.queueStore.fetch(id: job.id) { await self.notifyProgress(snapshot) }
        }
    }

    nonisolated func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didCompleteWithError error: Error?
    ) {
        let taskID = task.taskIdentifier
        lock.lock()
        let body = responseBuffers.removeValue(forKey: taskID) ?? Data()
        let waiter = partWaiters.removeValue(forKey: taskID)
        lock.unlock()
        let result = UploadPartResult(
            statusCode: (task.response as? HTTPURLResponse)?.statusCode ?? 0,
            body: body,
            error: error?.localizedDescription
        )
        if let waiter {
            waiter.resume(returning: result)
            return
        }
        // iOS may relaunch the process after the task was submitted. No
        // continuation exists in that process; keep the session and let Resume
        // reconcile the actual S3 parts instead of treating the task as lost.
        Task { [weak self] in
            guard let self, let job = await self.queueStore.fetch(byTaskIdentifier: taskID) else { return }
            _ = await self.queueStore.clearTask(id: job.id)
            _ = self.withLock { self.processingIDs.remove(job.id) }
            // A cancellation or terminal transition may have happened before
            // URLSession delivered this callback. Never resurrect that job.
            guard job.state.isActive else { return }
            if result.error == nil, (200..<300).contains(result.statusCode) {
                _ = await self.queueStore.updateState(id: job.id, state: .pending)
                await self.submit(jobID: job.id)
            } else {
                await self.fail(
                    jobID: job.id,
                    message: result.error ?? "HTTP \(result.statusCode)",
                    retryable: true
                )
            }
        }
    }

    nonisolated func urlSession(
        _ session: URLSession,
        dataTask: URLSessionDataTask,
        didReceive data: Data
    ) {
        let taskID = dataTask.taskIdentifier
        lock.lock()
        responseBuffers[taskID, default: Data()].append(data)
        lock.unlock()
    }

    // MARK: - Helpers

    @inline(__always)
    private func withLock<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    private func configuredTransfer() -> FileTransferService? {
        lock.lock(); defer { lock.unlock() }
        return transfer
    }

    private func configuredPostReadyHandler() -> UploadPostReadyHandler? {
        lock.lock(); defer { lock.unlock() }
        return postReadyHandler
    }

    private func token(for jobID: String) -> String? {
        lock.lock(); defer { lock.unlock() }
        return sessionTokens[jobID]
    }

    private func clearToken(jobID: String) {
        lock.lock(); sessionTokens.removeValue(forKey: jobID); lock.unlock()
    }

    private func currentTaskIdentifiers() async -> Set<Int> {
        await withCheckedContinuation { continuation in
            session.getAllTasks { tasks in continuation.resume(returning: Set(tasks.map(\.taskIdentifier))) }
        }
    }

    private func removeArtifacts(for job: UploadJobSnapshot) {
        UploadArtifactCleanup.remove(sourcePath: job.sourceFilePath, multipartPath: "")
        if let staging = UploadConstants.stagingDirectory {
            for part in 1...max(1, job.currentPart) {
                try? FileManager.default.removeItem(at: staging.appendingPathComponent("v2-\(job.id)-part-\(part).bin"))
            }
        }
    }

    private func notifyProgress(_ snapshot: UploadJobSnapshot) async {
        let listeners = listeners().progress
        await MainActor.run { listeners.forEach { $0(snapshot) } }
    }

    private func notifyCompletion(_ snapshot: UploadJobSnapshot) async {
        let listeners = listeners().completion
        await MainActor.run { listeners.forEach { $0(snapshot) } }
    }

    private func notifyFailure(_ snapshot: UploadJobSnapshot) async {
        let listeners = listeners().failure
        await MainActor.run { listeners.forEach { $0(snapshot) } }
    }

    private func listeners() -> (
        progress: [@MainActor @Sendable (UploadJobSnapshot) -> Void],
        completion: [@MainActor @Sendable (UploadJobSnapshot) -> Void],
        failure: [@MainActor @Sendable (UploadJobSnapshot) -> Void]
    ) {
        lock.lock(); defer { lock.unlock() }
        return (progressListeners, completionListeners, failureListeners)
    }

    private static func validPartAck(_ body: Data, partNumber: Int, size: Int64) -> Bool {
        guard let object = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
              let number = object["partNumber"] as? NSNumber,
              let receivedSize = object["size"] as? NSNumber else { return false }
        return number.intValue == partNumber && receivedSize.int64Value == size
    }

}

/// Потоковый SHA-256 для реального staged-файла. Буфер ограничен 4 MiB.
enum UploadFileHasher {
    static func hashAndSize(at url: URL) throws -> (sha256: String, size: Int64) {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        var size: Int64 = 0
        while let chunk = try handle.read(upToCount: 4 * 1024 * 1024), !chunk.isEmpty {
            hasher.update(data: chunk)
            size += Int64(chunk.count)
        }
        let digest = hasher.finalize().map { String(format: "%02x", $0) }.joined()
        return (digest, size)
    }
}

// MARK: - Shared V2 enqueue API (compiled into main app and Share Extension)

extension CloudRepository {
    @discardableResult
    func enqueueBackgroundUpload(
        sourceFile: URL,
        fileName: String,
        mimeType: String? = nil,
        toDirectory directoryID: String? = nil,
        source: UploadJobSource = .manual,
        routeByMediaKind: Bool = false,
        albumID: String? = nil,
        localIdentifier: String? = nil
    ) async throws -> String {
        // Защититься от гонки старта приложения: первая V2-задача не должна
        // появиться до отмены legacy queue и записи migration marker.
        await BackgroundUploadCoordinator.shared.migrateLegacyQueueIfNeeded()
        let staged: URL
        if let staging = UploadConstants.stagingDirectory,
           sourceFile.standardizedFileURL.path.hasPrefix(staging.standardizedFileURL.path + "/") {
            staged = sourceFile
        } else {
            staged = try UploadConstants.stageFile(sourceFile, fileName: fileName)
        }
        do {
            let digest = try UploadFileHasher.hashAndSize(at: staged)
            let intent: UploadPostReadyIntent
            if let albumID, !albumID.isEmpty {
                intent = .addToAlbum
            } else if routeByMediaKind {
                intent = .routeByMediaKind
            } else if let directoryID, !directoryID.isEmpty {
                intent = .attachDirectory
            } else {
                intent = .none
            }
            let snapshot = await UploadQueueStore.shared.createV2(
                source: source,
                sourceFilePath: staged.path,
                fileName: fileName,
                mimeType: mimeType ?? Self.inferredMimeType(for: fileName),
                totalBytes: digest.size,
                sha256: digest.sha256,
                directoryID: directoryID,
                intent: intent,
                albumID: albumID,
                localIdentifier: localIdentifier
            )
            await BackgroundUploadCoordinator.shared.submit(jobID: snapshot.id)
            return snapshot.id
        } catch {
            // The queue was not created, so no job can own this staged copy.
            // This is also safe when the caller supplied a file already inside
            // UploadStaging (all current callers transfer ownership on enqueue).
            try? FileManager.default.removeItem(at: staged)
            throw error
        }
    }

    private static func inferredMimeType(for fileName: String) -> String {
        let ext = (fileName as NSString).pathExtension
        return UTType(filenameExtension: ext)?.preferredMIMEType ?? "application/octet-stream"
    }

    @discardableResult
    func enqueueAndWaitForReady(
        sourceFile: URL,
        fileName: String,
        mimeType: String? = nil,
        toDirectory directoryID: String? = nil,
        source: UploadJobSource = .manual,
        routeByMediaKind: Bool = false,
        albumID: String? = nil,
        localIdentifier: String? = nil
    ) async throws -> String {
        let jobID = try await enqueueBackgroundUpload(
            sourceFile: sourceFile,
            fileName: fileName,
            mimeType: mimeType,
            toDirectory: directoryID,
            source: source,
            routeByMediaKind: routeByMediaKind,
            albumID: albumID,
            localIdentifier: localIdentifier
        )
        return try await BackgroundUploadCoordinator.shared.waitForReady(jobID: jobID)
    }
}
