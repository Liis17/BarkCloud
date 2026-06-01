import ActivityKit
import Foundation

/// Управляет одной агрегированной Live Activity для всех текущих загрузок.
/// Идея: одна активность на «сессию» — стартует с первым UploadJob, в неё
/// агрегируются все последующие, завершается через 3 с после того как все jobs
/// этой пачки попали в `completed` или `failed`.
///
/// `BackgroundUploadCoordinator` зовёт `notifyChanged()` после каждого события
/// (submit / progress / completed / failed). Контроллер пересчитывает агрегаты
/// из `UploadQueueStore.recentJobs(since:)` и обновляет Activity.
@MainActor
final class UploadLiveActivityController {
    static let shared = UploadLiveActivityController(queueStore: .shared)

    private let queueStore: UploadQueueStore

    private var currentActivity: Activity<UploadActivityAttributes>?
    private var sessionStartedAt: Date?
    /// main app в background. Когда true — Activity показывает «Откройте
    /// BarkCloud, чтобы продолжить» вместо прогресса. Управляется из
    /// `BarkCloudApp.scenePhase` через `setForegroundActive(_:)`.
    private var isInBackground = false

    init(queueStore: UploadQueueStore) {
        self.queueStore = queueStore
        // Подцепиться к Activity, начатой в Share Extension'е (если она ещё жива).
        // Иначе main app создал бы вторую активность, а старая зомби-висела бы
        // до staleDate.
        if let existing = Activity<UploadActivityAttributes>.activities.first {
            self.currentActivity = existing
            self.sessionStartedAt = existing.attributes.startedAt
        }
    }

    /// Вызывается из `BarkCloudApp` при смене scenePhase. true → main app
    /// активен (foreground), false → ушёл в background. Когда уходим в фон,
    /// сразу обновляем Activity на «Откройте BarkCloud…»; при возврате —
    /// обычный прогресс.
    func setForegroundActive(_ active: Bool) async {
        let newValue = !active
        guard newValue != isInBackground else { return }
        isInBackground = newValue
        await notifyChanged()
    }

    /// Вызвать после любого изменения очереди — контроллер сам решит, нужно
    /// ли стартовать / обновить / завершить активность.
    func notifyChanged() async {
        // Считаем агрегированный прогресс по всем jobs за последний час: это
        // охватывает текущую «сессию» (от старта Activity до её конца) и
        // отфильтровывает старые завершённые job из прошлых сессий.
        let since = sessionStartedAt ?? Date().addingTimeInterval(-3600)
        let jobs = await queueStore.recentJobs(since: since)
        guard !jobs.isEmpty else {
            await endIfNeeded()
            return
        }

        let activeStates: Set<UploadJobState> = [.pending, .preparing, .running]
        let activeJobs = jobs.filter { activeStates.contains($0.state) }

        // Зафиксировать окно сессии на createdAt самого раннего активного job,
        // иначе следующий вызов notifyChanged отсечёт свои же jobs.
        if sessionStartedAt == nil, !activeJobs.isEmpty {
            let earliest = activeJobs.map(\.createdAt).min() ?? Date()
            sessionStartedAt = earliest.addingTimeInterval(-1)
        }

        let total = jobs.count
        let completed = jobs.filter { $0.state == .completed }.count
        let failed = jobs.filter { $0.state == .failed }.count
        let running = jobs.first { $0.state == .running || $0.state == .preparing }

        let currentFileName = running?.fileName ?? ""
        let currentProgress: Double
        if let running, running.totalBytes > 0 {
            currentProgress = Double(running.bytesSent) / Double(running.totalBytes)
        } else {
            currentProgress = 0
        }

        let totalBytes = jobs.reduce(Int64(0)) { $0 + max($1.totalBytes, 0) }
        let sentBytes = jobs.reduce(Int64(0)) { acc, j in
            switch j.state {
            case .completed: return acc + max(j.totalBytes, 0)
            default:         return acc + j.bytesSent
            }
        }
        let overallProgress = totalBytes > 0 ? min(1.0, Double(sentBytes) / Double(totalBytes)) : 0
        let isFinished = total > 0 && (completed + failed == total)

        let state = UploadActivityAttributes.ContentState(
            totalFiles: total,
            completedFiles: completed,
            failedFiles: failed,
            currentFileName: currentFileName,
            currentProgress: currentProgress,
            overallProgress: overallProgress,
            isFinished: isFinished,
            requiresForeground: isInBackground ? true : nil
        )

        if currentActivity == nil, !isFinished {
            await start(initialState: state)
        } else if let activity = currentActivity {
            await activity.update(ActivityContent(state: state, staleDate: nil))
            if isFinished {
                await activity.end(
                    ActivityContent(state: state, staleDate: nil),
                    dismissalPolicy: .after(Date().addingTimeInterval(3))
                )
                currentActivity = nil
                sessionStartedAt = nil
            }
        }
    }

    private func start(initialState: UploadActivityAttributes.ContentState) async {
        guard ActivityAuthorizationInfo().areActivitiesEnabled else { return }
        // ВНИМАНИЕ: НЕ перезаписываем sessionStartedAt здесь. notifyChanged уже
        // зафиксировал его на min(activeJobs.createdAt)-1s раньше; если задать
        // на Date() сейчас — следующий recentJobs(since:) отсечёт собственные
        // job (их createdAt < startedAt на 50-100ms), recompute увидит
        // jobs.isEmpty и сразу же закроет Activity (Dynamic Island мигнёт и
        // погаснет, что и наблюдалось как «не работает»).
        let attributeStart = sessionStartedAt ?? Date()
        do {
            currentActivity = try Activity<UploadActivityAttributes>.request(
                attributes: UploadActivityAttributes(startedAt: attributeStart),
                content: ActivityContent(state: initialState, staleDate: nil),
                pushType: nil
            )
        } catch {
            // Лимит активных Live Activity исчерпан или пользователь отключил —
            // оставим без активности, UI сам покажет прогресс из координатора.
        }
    }

    private func endIfNeeded() async {
        guard let activity = currentActivity else { return }
        await activity.end(activity.content, dismissalPolicy: .immediate)
        currentActivity = nil
        sessionStartedAt = nil
    }
}
