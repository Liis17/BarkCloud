import Foundation
import Observation

/// Наблюдает за `BackgroundUploadCoordinator` и держит агрегированное состояние
/// текущей сессии загрузки для UI (`GlobalUploadBanner` над TabBar). Источник
/// истины — `UploadQueueStore.recentJobs(since:)`: смотрим только то, что было
/// создано после старта текущей сессии, чтобы старые завершённые job не висели.
///
/// Запускается из `AppEnvironment` (`addObserver(progress:completion:failure:)`).
/// Подписка живёт всё время жизни приложения.
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
    /// Источник самого свежего активного job (для иконки в баннере).
    private(set) var currentSource: UploadJobSource = .manual

    private let queueStore: UploadQueueStore
    /// Когда вошли в текущую сессию (первый job после простоя). nil — пока
    /// активной сессии нет. Используется как фильтр `recentJobs(since:)`.
    private var sessionStartedAt: Date?
    /// Дебаунс recompute: didSendBodyData приходит 50+ раз в секунду на большом
    /// файле, а пересчёт лезет в SwiftData. Достаточно ~10 fps для UI.
    private var pendingRecompute: Task<Void, Never>?

    init(queueStore: UploadQueueStore) {
        self.queueStore = queueStore
    }

    /// Подписаться на координатор. Все события идут через единый `recompute()` —
    /// прогресс лишь триггерит дебаунс, completion/failure — пересчитывают сразу.
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
        // Окно «недавно»: если сессии ещё не было — берём 1 час, чтобы зацепить
        // только что созданный job. После первого появления активного job окно
        // фиксируется этим моментом — старые завершённые отсекаются.
        let since = sessionStartedAt ?? Date().addingTimeInterval(-3600)
        let jobs = await queueStore.recentJobs(since: since)
        if jobs.isEmpty {
            reset()
            return
        }

        let activeStates: Set<UploadJobState> = [.pending, .preparing, .running]
        let hasActive = jobs.contains { activeStates.contains($0.state) }

        if sessionStartedAt == nil, hasActive {
            // Фиксируем окно сессии: чуть назад во времени, чтобы первый job
            // гарантированно попадал в фильтр `recentJobs(since:)`.
            sessionStartedAt = Date().addingTimeInterval(-1)
        }

        let total = jobs.count
        let completed = jobs.filter { $0.state == .completed }.count
        let failed = jobs.filter { $0.state == .failed }.count
        let running = jobs.first { $0.state == .running || $0.state == .preparing }

        let totalBytes = jobs.reduce(Int64(0)) { $0 + max($1.totalBytes, 0) }
        let sentBytes = jobs.reduce(Int64(0)) { acc, j in
            switch j.state {
            case .completed: return acc + max(j.totalBytes, 0)
            default:         return acc + j.bytesSent
            }
        }
        let overall = totalBytes > 0 ? min(1.0, Double(sentBytes) / Double(totalBytes)) : 0
        let finished = completed + failed == total

        totalFiles = total
        completedFiles = completed
        failedFiles = failed
        currentFileName = running?.fileName ?? jobs.last?.fileName ?? ""
        overallProgress = overall
        currentSource = running?.source ?? jobs.last?.source ?? .manual
        isActive = !finished

        if finished {
            // Дать пользователю секунду посмотреть «100%», потом скрыть.
            Task { [weak self] in
                try? await Task.sleep(nanoseconds: 1_500_000_000)
                self?.reset()
            }
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
    }
}
