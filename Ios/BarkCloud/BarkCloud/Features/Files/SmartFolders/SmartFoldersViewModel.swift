import Foundation
import Observation
import BarkCloudKit

/// Список умных папок для секции-сетки в табе «Файлы». Загружает системные +
/// пользовательские папки; удаление пользовательской — точечно с перезагрузкой.
@MainActor
@Observable
final class SmartFoldersViewModel {
    var folders: [DynamicFolderCard] = []
    var isLoading = true

    private var repo: DynamicFolderRepository?
    private var didLoad = false

    func load(repo: DynamicFolderRepository) async {
        self.repo = repo
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    func reload() async {
        guard let repo else { return }
        do {
            folders = try await repo.listFolders()
        } catch {
            // Тихо: при ошибке секция просто не показывается.
            folders = []
        }
        isLoading = false
    }

    func delete(_ folder: DynamicFolderCard) async {
        guard let repo, !folder.isSystem else { return }
        try? await repo.delete(folderID: folder.id)
        await reload()
    }
}
