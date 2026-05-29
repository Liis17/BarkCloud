import Foundation
import Observation

/// Догружает файлы, накопленные Share Extension в общем контейнере App Group.
/// Работает на переднем плане (как автозагрузка резервной копии — фоновой
/// `URLSession` в проекте нет): запускается при старте и при возврате приложения
/// на передний план, если есть валидная сессия.
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
        Task {
            // Привязываем к авто-папке «Недавно загруженные» (как остальные загрузки).
            let folderID = try? await cloud.ensureRecentUploadsFolder()
            for item in items {
                do {
                    let data = try Data(contentsOf: item)
                    _ = try await cloud.uploadFile(data: data, fileName: item.lastPathComponent, toDirectory: folderID)
                    ShareInbox.remove(item)
                } catch {
                    // Оставляем файл в ящике до следующей попытки.
                }
            }
            isRunning = false
        }
    }
}
