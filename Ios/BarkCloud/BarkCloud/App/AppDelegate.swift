import BackgroundTasks
import UIKit

/// Подключается через `@UIApplicationDelegateAdaptor` в `BarkCloudApp`. Нужен
/// ровно по двум причинам:
/// 1. Принять completion-handler от системы, когда iOS будит main app в фоне для
///    доставки делегатных событий background URLSession
///    (`handleEventsForBackgroundURLSession`).
/// 2. Зарегистрировать BGTask-хендлер для retry упавших загрузок.
final class AppDelegate: NSObject, UIApplicationDelegate {
    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        BGTaskScheduler.shared.register(
            forTaskWithIdentifier: UploadConstants.retryBGTaskIdentifier,
            using: nil
        ) { [weak self] task in
            self?.handleRetryTask(task as! BGProcessingTask) ?? task.setTaskCompleted(success: false)
        }
        return true
    }

    func application(
        _ application: UIApplication,
        handleEventsForBackgroundURLSession identifier: String,
        completionHandler: @escaping () -> Void
    ) {
        guard identifier == UploadConstants.uploadSessionIdentifier else {
            completionHandler()
            return
        }
        BackgroundUploadCoordinator.shared.setBackgroundCompletionHandler(completionHandler)
    }

    // MARK: - BGTask retry

    /// iOS просыпается по нашему BGProcessingTaskRequest (запланирован после
    /// падения загрузки). Перевыставляем сетевые failed jobs с `retries < maxRetries`
    /// в pending, а для `uploadedNotAttached` повторяем только AttachFile.
    /// Реальная передача байт уйдёт в background
    /// URLSession, которая работает независимо от life-cycle'а нашего процесса.
    private func handleRetryTask(_ task: BGProcessingTask) {
        let work = Task {
            let retryable = await UploadQueueStore.shared.retryableJobs(maxRetries: UploadConstants.maxUploadRetries)
            for snapshot in retryable {
                let started: Bool
                if snapshot.state == .uploadedNotAttached {
                    started = await BackgroundUploadCoordinator.shared.submitAndWaitForBackgroundStart(jobID: snapshot.id)
                } else {
                    await UploadQueueStore.shared.incrementRetries(id: snapshot.id)
                    await UploadQueueStore.shared.resetForRetry(id: snapshot.id)
                    started = await BackgroundUploadCoordinator.shared.submitAndWaitForBackgroundStart(jobID: snapshot.id)
                }
                if !started {
                    // Four transfer slots may still be occupied. Keep the
                    // retry request alive; the normal scheduler intentionally
                    // does not auto-pick uploadedNotAttached jobs.
                    scheduleRetryBGTaskIfNeeded()
                }
            }
            task.setTaskCompleted(success: true)
        }
        task.expirationHandler = { work.cancel() }
    }
}

/// Запросить у системы повторный заход для retry. Безопасно идемпотентен —
    /// BGTaskScheduler сам дедуплицирует запросы по identifier'у.
@MainActor
func scheduleRetryBGTaskIfNeeded() {
    let request = BGProcessingTaskRequest(identifier: UploadConstants.retryBGTaskIdentifier)
    request.requiresNetworkConnectivity = true
    request.requiresExternalPower = false
    request.earliestBeginDate = Date(timeIntervalSinceNow: 5 * 60)
    try? BGTaskScheduler.shared.submit(request)
}
