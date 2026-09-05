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
    /// падения загрузки). Перевыставляем все failed jobs с `retries < maxRetries`
    /// в pending и пере-submit'им. Реальная передача байт уйдёт в background
    /// URLSession, которая работает независимо от life-cycle'а нашего процесса.
    private func handleRetryTask(_ task: BGProcessingTask) {
        let work = Task {
            let failed = await UploadQueueStore.shared.failedJobs(maxRetries: UploadConstants.maxUploadRetries)
            for snapshot in failed {
                await UploadQueueStore.shared.incrementRetries(id: snapshot.id)
                await UploadQueueStore.shared.resetForRetry(id: snapshot.id)
                await BackgroundUploadCoordinator.shared.submit(jobID: snapshot.id)
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
