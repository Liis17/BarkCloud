import Foundation
import Observation

/// Держит агрегированное состояние текущей загрузки для глобального баннера над
/// TabBar ([[GlobalUploadBanner]]).
///
/// Источник истины зависит от типа загрузки:
/// - **Автозагрузка медиатеки** — счётчики `BackupManager` напрямую (как
///   [[BackupSheet]], кнопка облака в Галерее). Это критично: `BackupManager`
///   подаёт задачи в URLSession порциями (`inFlightLimit`), поэтому в самой
///   очереди в любой момент лежит лишь 1–5 задач, хотя в `pendingUpload` их могут
///   быть десятки. Если считать total по очереди — баннер «застревает на первом
///   файле». `BackupManager` же знает весь бэклог и ведёт монотонные in-memory
///   счётчики, которые не отравляются «осиротевшими» job из прошлых сессий.
/// - **Ручные / share-загрузки** — из `UploadQueueStore.recentJobs(since:)`
///   (бэкап отфильтрован).
///
/// Запускается из `AppEnvironment` (`attach(to:)`). Подписка живёт всё время
/// жизни приложения.
@MainActor
@Observable
final class UploadProgressObserver {
    /// Агрегаты текущей сессии (для UI). `isActive == false` — баннер скрыт.
    private(set) var isActive = false
    private(set) var totalFiles = 0
    private(set) var completedFiles = 0
    private(set) var failedFiles = 0
    private(set) var currentFileName = ""
    private(set) var overallProgress: Double = 0
    /// Источник самого свежего активного job (для иконки/заголовка в баннере).
    private(set) var currentSource: UploadJobSource = .manual

    private let queueStore: UploadQueueStore
    private let backupManager: BackupManager
    /// Когда вошли в текущую сессию ручных/share-загрузок (фильтр `recentJobs`).
    private var sessionStartedAt: Date?
    /// Дебаунс recompute: didSendBodyData приходит 50+ раз в секунду на большом
    /// файле, а пересчёт лезет в SwiftData. Достаточно ~10 fps для UI.
    private var pendingRecompute: Task<Void, Never>?
    /// Отложенное скрытие баннера после завершения. Cancellable — если в течение
    /// 1.5с появится новая активность, отменяем, иначе баннер исчезал бы даже
    /// когда уже идёт следующая загрузка.
    private var resetTask: Task<Void, Never>?

    init(queueStore: UploadQueueStore, backupManager: BackupManager) {
        self.queueStore = queueStore
        self.backupManager = backupManager
    }

    /// Подписаться на координатор. Прогресс лишь триггерит дебаунс,
    /// completion/failure — пересчитывают сразу.
    func attach(to coordinator: BackgroundUploadCoordinator) {
        coordinator.addObserver(
            progress: { [weak self] _ in self?.scheduleRecompute() },
            completion: { [weak self] _ in self?.recomputeNow() },
            failure: { [weak self] _ in self?.recomputeNow() }
        )
        recomputeNow()
    }

    private func scheduleRecompute() {
        guard pendingRecompute == nil else { return }
        pendingRecompute = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 100_000_000)
            self?.pendingRecompute = nil
            self?.recomputeNow()
        }
    }

    private func recomputeNow() {
        Task { await self.recompute() }
    }

    private func recompute() async {
        observeBackupChanges()

        // Приоритет — автозагрузка медиатеки (её счётчики авторитетны).
        if let agg = backupAggregate() {
            apply(agg)
            return
        }
        // Иначе — ручные/share-загрузки из очереди URLSession.
        if let agg = await queueAggregate() {
            apply(agg)
            return
        }
        // Активных загрузок нет. Если баннер ещё виден — показать завершённое
        // состояние («N/N, 100%») мгновение, затем скрыть.
        if isActive {
            completedFiles = max(completedFiles, totalFiles - failedFiles)
            overallProgress = totalFiles > 0 ? 1 : overallProgress
            scheduleReset()
        } else {
            reset()
        }
    }

    private struct Aggregate {
        let total: Int
        let completed: Int
        let failed: Int
        let fileName: String
        let progress: Double
        let source: UploadJobSource
    }

    // MARK: - Автозагрузка (in-memory счётчики BackupManager)

    private func backupAggregate() -> Aggregate? {
        let m = backupManager
        guard m.remainingCount > 0 else { return nil }
        let total = m.uploadDone + m.uploadFailed + m.remainingCount
        let progress = total > 0 ? Double(m.uploadDone) / Double(total) : 0
        return Aggregate(
            total: total,
            completed: m.uploadDone,
            failed: m.uploadFailed,
            fileName: m.currentFileName,
            progress: progress,
            source: .backup
        )
    }

    /// Перезапускать recompute при изменении счётчиков `BackupManager`, чтобы
    /// баннер обновлялся синхронно с модалкой, а не только по событиям URLSession
    /// (которых для уже поданных, но ещё не стартовавших задач может не быть).
    /// `withObservationTracking` одноразовый — повторная регистрация идёт из
    /// recompute, образуя непрерывную цепочку.
    private func observeBackupChanges() {
        withObservationTracking {
            let m = backupManager
            _ = m.uploadDone
            _ = m.uploadFailed
            _ = m.remainingCount
            _ = m.currentFileName
        } onChange: { [weak self] in
            Task { @MainActor in self?.recomputeNow() }
        }
    }

    // MARK: - Ручные / share-загрузки (из очереди URLSession)

    private func queueAggregate() async -> Aggregate? {
        // Окно «недавно»: до старта сессии — 1 час (зацепить только что созданный
        // job). После — фиксируется минимальным createdAt активных.
        let since = sessionStartedAt ?? Date().addingTimeInterval(-3600)
        let jobs = (await queueStore.recentJobs(since: since)).filter { $0.source != .backup }
        guard !jobs.isEmpty else {
            sessionStartedAt = nil
            return nil
        }

        let activeStates: Set<UploadJobState> = [.pending, .preparing, .running]
        let activeJobs = jobs.filter { activeStates.contains($0.state) }
        // Осиротевшие `.running` (их background-task умер) баннер не держат —
        // иначе один такой job навсегда оставлял бы баннер.
        let blockingJobs = await BackgroundUploadCoordinator.shared.blockingActiveJobs(from: activeJobs)
        guard !blockingJobs.isEmpty else {
            sessionStartedAt = nil
            return nil
        }

        if sessionStartedAt == nil {
            let earliest = blockingJobs.map(\.createdAt).min() ?? Date()
            sessionStartedAt = earliest.addingTimeInterval(-1)
        }

        let total = jobs.count
        let completed = jobs.filter { $0.state == .completed }.count
        let failed = jobs.filter { $0.state == .failed }.count
        let running = jobs.first { $0.state == .running || $0.state == .preparing }

        let totalBytes = jobs.reduce(Int64(0)) { $0 + max($1.totalBytes, 0) }
        let sentBytes = jobs.reduce(Int64(0)) { acc, j in
            j.state == .completed ? acc + max(j.totalBytes, 0) : acc + j.bytesSent
        }
        let progress = totalBytes > 0 ? min(1.0, Double(sentBytes) / Double(totalBytes)) : 0

        return Aggregate(
            total: total,
            completed: completed,
            failed: failed,
            fileName: running?.fileName ?? jobs.last?.fileName ?? "",
            progress: progress,
            source: running?.source ?? jobs.last?.source ?? .manual
        )
    }

    // MARK: - Применение / скрытие

    private func apply(_ agg: Aggregate) {
        resetTask?.cancel()
        resetTask = nil
        totalFiles = agg.total
        completedFiles = agg.completed
        failedFiles = agg.failed
        currentFileName = agg.fileName
        overallProgress = agg.progress
        currentSource = agg.source
        isActive = true
    }

    private func scheduleReset() {
        guard resetTask == nil else { return }
        resetTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            guard !Task.isCancelled else { return }
            self?.reset()
        }
    }

    private func reset() {
        isActive = false
        totalFiles = 0
        completedFiles = 0
        failedFiles = 0
        currentFileName = ""
        overallProgress = 0
        sessionStartedAt = nil
        resetTask = nil
    }
}
