import Foundation
import Observation

/// Одноразовая миграция legacy `ShareInbox/<uuid>/<file>` (формат старой версии
/// Share Extension, который только складывал файлы в App Group). Теперь Share
/// Extension сразу ставит UploadJob в `BackgroundUploadCoordinator`, но если
/// пользователь обновился со старыми файлами в очереди — переоформляем их в
/// новый формат, не теряя.
///
/// Запускается при старте app и при возврате на передний план. После того как
/// пользователи проапгрейдятся, этот класс можно удалить.
@MainActor
@Observable
final class ShareInboxUploader {
    private let cloud: CloudRepository
    private let session: SessionStore
    private var isRunning = false

    init(cloud: CloudRepository, session: SessionStore) {
        self.cloud = cloud
        self.session = session
    }

    func uploadPendingIfNeeded() {
        guard !isRunning, session.hasValidRefreshToken() else { return }
        let items = ShareInbox.pendingItems()
        guard !items.isEmpty else { return }
        isRunning = true
        Task { [cloud] in
            // Привязываем к авто-папке «Недавно загруженные», как остальные загрузки.
            let folderID = try? await cloud.ensureRecentUploadsFolder()
            for item in items {
                do {
                    _ = try await cloud.enqueueBackgroundUpload(
                        sourceFile: item,
                        fileName: item.lastPathComponent,
                        toDirectory: folderID,
                        source: .share
                    )
                    ShareInbox.remove(item)
                } catch {
                    // Оставляем файл в ящике до следующей попытки.
                }
            }
            isRunning = false
        }
    }
}
