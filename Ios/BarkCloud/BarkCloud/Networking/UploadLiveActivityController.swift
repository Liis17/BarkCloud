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

    init(queueStore: UploadQueueStore) {
        self.queueStore = queueStore
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
            isFinished: isFinished
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
        let startedAt = Date()
        sessionStartedAt = startedAt
        do {
            currentActivity = try Activity<UploadActivityAttributes>.request(
                attributes: UploadActivityAttributes(startedAt: startedAt),
                content: ActivityContent(state: initialState, staleDate: nil),
                pushType: nil
            )
        } catch {
            // Лимит активных Live Activity исчерпан или пользователь отключил —
            // оставим без активности, UI сам покажет прогресс из координатора.
            sessionStartedAt = nil
        }
    }

    private func endIfNeeded() async {
        guard let activity = currentActivity else { return }
        await activity.end(activity.content, dismissalPolicy: .immediate)
        currentActivity = nil
        sessionStartedAt = nil
    }
}
