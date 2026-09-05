import Foundation
import BarkCloudKit

/// Замыкание, возвращающее свежий access-token. main app и Share Extension
/// устанавливают своё (через `GrpcManager.validAccessToken`).
typealias UploadTokenProvider = @Sendable () async -> String?

/// Координатор фоновой загрузки. Поверх одной `URLSession` с background-конфигом —
/// он же делегат: получает прогресс, разбирает ответ сервера, обновляет UploadJob
/// и пробрасывает события наружу (`onJobProgress`/`onJobCompleted`/`onJobFailed`)
/// для Live Activity и UI.
///
/// Один и тот же `identifier` сессии используется в main app и в Share Extension.
/// iOS-демон ведёт фактическую передачу байт, поэтому при сворачивании/убийстве
/// приложения загрузка продолжается. При завершении система запускает main app
/// в фоне через `application(_:handleEventsForBackgroundURLSession:)` →
/// `setBackgroundCompletionHandler(_:)`.
final class BackgroundUploadCoordinator: NSObject, @unchecked Sendable {
    static let shared = BackgroundUploadCoordinator(queueStore: .shared)

    private let queueStore: UploadQueueStore

    /// Single hooks (managed: ровно один setter). tokenProvider — кто выдаёт
    /// x-auth-token; onPersistentFailure — планировщик BGTask retry (в Share
    /// Extension `BackgroundTasks` недоступен, остаётся nil).
    var tokenProvider: UploadTokenProvider?
    var onPersistentFailure: (@MainActor @Sendable () -> Void)?

    /// Множественные observer'ы. Используются и для системных хуков
    /// (AppEnvironment.attachFile при completed), и для UI-наблюдателей
    /// (`UploadProgressObserver` рендерит глобальный баннер). Доступ под `lock`.
    private var progressListeners: [@MainActor @Sendable (UploadJobSnapshot) -> Void] = []
    private var completionListeners: [@MainActor @Sendable (UploadJobSnapshot) -> Void] = []
    private var failureListeners: [@MainActor @Sendable (UploadJobSnapshot) -> Void] = []

    private let lock = NSLock()
    private var responseBuffers: [Int: Data] = [:]
    private var backgroundCompletionHandler: (() -> Void)?

    /// Подписать слушателя на события очереди. Зовётся из `AppEnvironment` (хук
    /// attachFile) и `UploadProgressObserver` (глобальный баннер).
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

    private func snapshotListeners() -> (
        progress: [@MainActor @Sendable (UploadJobSnapshot) -> Void],
        completion: [@MainActor @Sendable (UploadJobSnapshot) -> Void],
        failure: [@MainActor @Sendable (UploadJobSnapshot) -> Void]
    ) {
        lock.lock()
        defer { lock.unlock() }
        return (progressListeners, completionListeners, failureListeners)
    }

    private(set) lazy var session: URLSession = {
        let config = URLSessionConfiguration.background(withIdentifier: UploadConstants.uploadSessionIdentifier)
        config.sharedContainerIdentifier = UploadConstants.appGroupID
        config.isDiscretionary = false
        config.sessionSendsLaunchEvents = true
        config.allowsCellularAccess = true
        config.timeoutIntervalForRequest = 60
        config.timeoutIntervalForResource = 60 * 60
        return URLSession(configuration: config, delegate: self, delegateQueue: nil)
    }()

    init(queueStore: UploadQueueStore) {
        self.queueStore = queueStore
        super.init()
    }

    // MARK: - Background launch

    /// Вызывается из `AppDelegate.application(_:handleEventsForBackgroundURLSession:completionHandler:)`.
    /// Coordinator сохраняет handler и зовёт его в `urlSessionDidFinishEvents(...)`.
    func setBackgroundCompletionHandler(_ handler: @escaping () -> Void) {
        lock.lock()
        backgroundCompletionHandler = handler
        lock.unlock()
        _ = session  // запустить lazy инициализацию
    }

    // MARK: - Submit

    /// Подать UploadJob в фоновую сессию. Создаёт `uploadTask(with:fromFile:)` и
    /// обновляет `sessionTaskIdentifier`.
    func submit(jobID: String) async {
        guard let request = await prepareRequest(jobID: jobID),
              let multipartURL = await multipartFileURL(jobID: jobID) else {
            if let snapshot = await queueStore.fetch(id: jobID) {
                UploadArtifactCleanup.remove(
                    sourcePath: snapshot.sourceFilePath,
                    multipartPath: snapshot.multipartBodyPath
                )
            }
            _ = await queueStore.updateState(id: jobID, state: .failed, lastError: "Bad upload metadata")
            if let snapshot = await queueStore.fetch(id: jobID) {
                await notifyFailure(snapshot)
            }
            await UploadLiveActivityController.shared.notifyChanged()
            return
        }
        let task = session.uploadTask(with: request, fromFile: multipartURL)
        _ = await queueStore.attachTask(jobID: jobID, taskIdentifier: task.taskIdentifier)
        task.resume()
        await UploadLiveActivityController.shared.notifyChanged()
    }

    private func prepareRequest(jobID: String) async -> URLRequest? {
        guard let snapshot = await queueStore.fetch(id: jobID),
              let url = URL(string: snapshot.uploadURL) else { return nil }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue(
            "multipart/form-data; boundary=\(UploadConstants.multipartBoundary)",
            forHTTPHeaderField: "Content-Type"
        )
        if let token = await tokenProvider?(), !token.isEmpty {
            request.setValue(token, forHTTPHeaderField: "x-auth-token")
        }
        return request
    }

    private func multipartFileURL(jobID: String) async -> URL? {
        guard let snapshot = await queueStore.fetch(id: jobID),
              FileManager.default.fileExists(atPath: snapshot.multipartBodyPath) else { return nil }
        return URL(fileURLWithPath: snapshot.multipartBodyPath)
    }

    // MARK: - Attach (re-cinching on app start)

    /// При старте main app: прицепиться к существующей background-сессии и привести
    /// очередь в порядок.
    /// - Задачи, которые ещё живы (in-flight task пережил перезапуск в фоновом
    ///   демоне) — оставляем как есть.
    /// - Любой осиротевший job (нет живого task) — **удаляем, НЕ перезаливаем**.
    ///   Возобновить его нельзя: `uploadURL` одноразовый/протухший, повторный POST
    ///   на него падает → вечный retry и фантомная панель «1 из N» на уже
    ///   загруженных файлах. Реально недостающие файлы заново подберёт скан
    ///   BackupManager (дедуп по SHA256 — дубликатов не будет) со свежим uploadURL.
    func attachAndResubmitOrphans() async {
        _ = self.session
        // Подчистить давно завершённые jobs, чтобы они не копились и не висели в
        // часовом окне recentJobs (источник прогресса баннера/Live Activity).
        await queueStore.purgeCompleted(olderThan: Date().addingTimeInterval(-600))
        let liveIdentifiers = await currentTaskIdentifiers()
        // Зовётся и на каждом возврате в foreground, поэтому свежие jobs (этой
        // сессии) не трогаем: BackupManager/ShareInbox прямо сейчас их создают и
        // сабмитят, удаление гонилось бы с submit и «съедало» новые загрузки.
        let staleCutoff = Date().addingTimeInterval(-60)
        let active = await queueStore.activeJobs()
        let retryable = await queueStore.failedJobs(maxRetries: UploadConstants.maxUploadRetries)
        var referencedPaths = Set(retryable.flatMap { [$0.sourceFilePath, $0.multipartBodyPath] })
        for snapshot in active {
            let hasLiveTask = snapshot.state == .running
                && liveIdentifiers.contains(snapshot.sessionTaskIdentifier)
            guard hasLiveTask || snapshot.createdAt >= staleCutoff else {
                UploadArtifactCleanup.remove(
                    sourcePath: snapshot.sourceFilePath,
                    multipartPath: snapshot.multipartBodyPath
                )
                await queueStore.delete(id: snapshot.id)
                continue
            }
            referencedPaths.formUnion([snapshot.sourceFilePath, snapshot.multipartBodyPath])
        }
        UploadConstants.purgeOrphanedStaging(referencedPaths: referencedPaths)
        // Обновить Live Activity и UI: если main app открылся после того как
        // Share Extension стартовал Activity и ушёл — controller подцепился к
        // существующей активности в init, но её state нужно освежить актуальным
        // snapshot'ом из SwiftData. Иначе Dynamic Island виснет на initial state.
        await UploadLiveActivityController.shared.notifyChanged()
    }

    private func currentTaskIdentifiers() async -> Set<Int> {
        await withCheckedContinuation { continuation in
            session.getAllTasks { tasks in
                continuation.resume(returning: Set(tasks.map(\.taskIdentifier)))
            }
        }
    }

    /// За сколько секунд без обновления активный job считаем «подзависшим» и
    /// проверяем, жив ли его background-task.
    private static let staleActiveAfter: TimeInterval = 15

    /// Из набора активных jobs вернуть те, что реально держат UI прогресса:
    /// недавно прогрессировавшие (`updatedAt` свежий) либо подзависшие, но с живым
    /// URLSession-task. Осиротевший `.running` (его task умер с прошлым запуском
    /// приложения, событий по нему уже не будет) исключается — иначе он навсегда
    /// держал бы баннер и Live Activity, и `completed+failed` никогда не сравнялось
    /// бы с `total`. getAllTasks дёргаем только если есть подзависшие — на горячем
    /// пути активной загрузки (свежие jobs) лишних системных вызовов нет.
    func blockingActiveJobs(from active: [UploadJobSnapshot]) async -> [UploadJobSnapshot] {
        guard !active.isEmpty else { return [] }
        let cutoff = Date().addingTimeInterval(-Self.staleActiveAfter)
        let stale = active.filter { $0.updatedAt <= cutoff }
        guard !stale.isEmpty else { return active }
        let live = await currentTaskIdentifiers()
        return active.filter { job in
            guard job.updatedAt <= cutoff else { return true }
            return job.state == .running ? live.contains(job.sessionTaskIdentifier) : true
        }
    }

    /// Отменить все живые задачи фоновой сессии и очистить буферы ответов —
    /// при полном сбросе, чтобы загрузки не продолжались с токеном прежнего аккаунта.
    func cancelAll() async {
        let tasks: [URLSessionTask] = await withCheckedContinuation { continuation in
            session.getAllTasks { continuation.resume(returning: $0) }
        }
        for task in tasks { task.cancel() }
        lock.lock()
        responseBuffers.removeAll()
        lock.unlock()
    }

    /// Отменить активные задачи определённого источника (`.backup`, `.share`,
    /// `.manual`). Используется при выключении автозагрузки: останавливаем
    /// только backup-jobs, ручные/из шаринга продолжают работать. Каждый
    /// отменённый job помечается `.failed` в БД, чтобы UI и Live Activity
    /// освежились.
    func cancelActiveJobs(source: UploadJobSource) async {
        let active = await queueStore.activeJobs().filter { $0.source == source }
        guard !active.isEmpty else { return }
        let taskIDs = Set(active.map { $0.sessionTaskIdentifier })
        let tasks: [URLSessionTask] = await withCheckedContinuation { continuation in
            session.getAllTasks { continuation.resume(returning: $0) }
        }
        for task in tasks where taskIDs.contains(task.taskIdentifier) {
            task.cancel()
        }
        for snapshot in active {
            _ = await queueStore.updateState(
                id: snapshot.id,
                state: .failed,
                lastError: "Cancelled by user"
            )
            UploadArtifactCleanup.remove(
                sourcePath: snapshot.sourceFilePath,
                multipartPath: snapshot.multipartBodyPath
            )
        }
        await UploadLiveActivityController.shared.notifyChanged()
    }
}

// MARK: - URLSessionDelegate (TLS + background launch finish)

extension BackgroundUploadCoordinator: URLSessionDelegate {
    nonisolated func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        // Зеркалит `SelfSignedTrustDelegate` из InsecureURLSession.swift — но для
        // background-сессии нужен свой делегат, в reference Apple-доке так и сделано.
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
        if let handler {
            DispatchQueue.main.async { handler() }
        }
    }
}

// MARK: - URLSessionTaskDelegate (прогресс + завершение)

extension BackgroundUploadCoordinator: URLSessionTaskDelegate {
    nonisolated func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didSendBodyData bytesSent: Int64,
        totalBytesSent: Int64,
        totalBytesExpectedToSend: Int64
    ) {
        let taskID = task.taskIdentifier
        Task { [weak self] in
            await self?.handleProgress(taskID: taskID, totalBytesSent: totalBytesSent, expected: totalBytesExpectedToSend)
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
        lock.unlock()
        let statusCode = (task.response as? HTTPURLResponse)?.statusCode ?? 0
        let errorDescription = error?.localizedDescription
        Task { [weak self] in
            await self?.handleCompletion(
                taskID: taskID,
                statusCode: statusCode,
                body: body,
                error: errorDescription
            )
        }
    }
}

// MARK: - URLSessionDataDelegate (накопление response body)

extension BackgroundUploadCoordinator: URLSessionDataDelegate {
    nonisolated func urlSession(
        _ session: URLSession,
        dataTask: URLSessionDataTask,
        didReceive data: Data
    ) {
        let taskID = dataTask.taskIdentifier
        lock.lock()
        var existing = responseBuffers[taskID] ?? Data()
        existing.append(data)
        responseBuffers[taskID] = existing
        lock.unlock()
    }
}

// MARK: - Internal helpers

private extension BackgroundUploadCoordinator {
    func handleProgress(taskID: Int, totalBytesSent: Int64, expected: Int64) async {
        guard let snapshot = await queueStore.fetch(byTaskIdentifier: taskID) else { return }
        let updated = await queueStore.updateProgress(id: snapshot.id, bytesSent: totalBytesSent, total: expected) ?? snapshot
        await notifyProgress(updated)
        await UploadLiveActivityController.shared.notifyChanged()
    }

    func handleCompletion(taskID: Int, statusCode: Int, body: Data, error: String?) async {
        guard let snapshot = await queueStore.fetch(byTaskIdentifier: taskID) else { return }
        let canRetry = snapshot.retries < UploadConstants.maxUploadRetries
            && onPersistentFailure != nil
        if let error {
            let updated = await queueStore.updateState(id: snapshot.id, state: .failed, lastError: error) ?? snapshot
            if !canRetry { removeArtifacts(for: snapshot) }
            await notifyFailure(updated)
            await UploadLiveActivityController.shared.notifyChanged()
            if canRetry { await notifyPersistentFailure() }
            return
        }
        guard (200..<300).contains(statusCode) else {
            let updated = await queueStore.updateState(id: snapshot.id, state: .failed, lastError: "HTTP \(statusCode)") ?? snapshot
            if !canRetry { removeArtifacts(for: snapshot) }
            await notifyFailure(updated)
            await UploadLiveActivityController.shared.notifyChanged()
            if canRetry { await notifyPersistentFailure() }
            return
        }
        removeArtifacts(for: snapshot)
        let returnedFileID = Self.parseFileID(from: body)
        let updated = await queueStore.updateState(
            id: snapshot.id,
            state: .completed,
            lastError: nil,
            returnedFileID: returnedFileID
        ) ?? snapshot
        await notifyCompletion(updated)
        await UploadLiveActivityController.shared.notifyChanged()
    }

    // MARK: - Notify (под `lock` снимаем массив, потом дёргаем на MainActor)

    func notifyProgress(_ snapshot: UploadJobSnapshot) async {
        let listeners = snapshotListeners().progress
        guard !listeners.isEmpty else { return }
        await MainActor.run { listeners.forEach { $0(snapshot) } }
    }

    func notifyCompletion(_ snapshot: UploadJobSnapshot) async {
        let listeners = snapshotListeners().completion
        guard !listeners.isEmpty else { return }
        await MainActor.run { listeners.forEach { $0(snapshot) } }
    }

    func notifyFailure(_ snapshot: UploadJobSnapshot) async {
        let listeners = snapshotListeners().failure
        guard !listeners.isEmpty else { return }
        await MainActor.run { listeners.forEach { $0(snapshot) } }
    }

    func notifyPersistentFailure() async {
        guard let hook = onPersistentFailure else { return }
        await MainActor.run { hook() }
    }

    func removeArtifacts(for snapshot: UploadJobSnapshot) {
        UploadArtifactCleanup.remove(
            sourcePath: snapshot.sourceFilePath,
            multipartPath: snapshot.multipartBodyPath
        )
    }

    static func parseFileID(from body: Data) -> String? {
        guard !body.isEmpty,
              let obj = try? JSONSerialization.jsonObject(with: body) as? [String: Any],
              let fid = obj["fileId"] as? String, !fid.isEmpty else { return nil }
        return fid
    }
}
