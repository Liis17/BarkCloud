import Foundation
import Observation
import UIKit
import BarkCloudKit

/// Состояние таба «Мне доступны» в `SharedHubScreen`.
struct SharedWithMeUiState {
    var items: [SharedFileEntry] = []
    /// Папки, которыми со мной поделились (бэкенд не пагинирует). Навигация по
    /// поддереву — отдельным экраном `SharedFolderBrowserScreen`.
    var folders: [SharedFolderItem] = []
    /// Резолв имён владельцев: ownerUserID → CloudUser. Если в словаре нет —
    /// рендер показывает `id N` (фоллбек `CloudUser` с пустыми полями).
    var owners: [Int64: CloudUser] = [:]
    var isPlaceholder: Bool = true
    var isLoadingMore: Bool = false
    var canLoadMore: Bool = false
    /// fileID, для которого сейчас идёт «Скачать» (показываем spinner вместо кнопки).
    var downloading: Set<String> = []
    var snackbar: String?
    /// Когда установлено — Screen открывает системный `UIDocumentPickerViewController`
    /// для сохранения скачанного файла в Files / iCloud / куда угодно.
    var pendingExportFile: URL?

    /// Пусто, когда нет ни файлов, ни папок.
    var isEmpty: Bool { items.isEmpty && folders.isEmpty }

    fileprivate var cursorSharedAt: Date?
    fileprivate var cursorGrantID: String = ""
}

/// View-model раздела «Мне доступны»: пагинируемый список входящих шаров +
/// скачивание через `URLSession.shared.download`. После скачивания выставляем
/// `pendingExportFile` — Screen открывает `UIDocumentPickerViewController` для
/// сохранения; временный файл удаляется после закрытия пикера.
@MainActor
@Observable
final class SharedWithMeViewModel {
    var state = SharedWithMeUiState()

    private let cloud: CloudRepository
    private let users: UserRepository
    private var didLoad = false

    init(cloud: CloudRepository, users: UserRepository) {
        self.cloud = cloud
        self.users = users
    }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    func reload() async {
        do {
            let page = try await cloud.listSharedWithMe(limit: 60)
            state.items = page.items
            state.cursorSharedAt = page.nextCursorSharedAt
            state.cursorGrantID = page.nextCursorGrantID
            state.canLoadMore = page.hasMore
            await resolveUsers(Set(page.items.map(\.ownerUserID)))
        } catch {
            state.items = []
            state.snackbar = String(localized: "shared_load_failed")
        }
        // Доступные папки — best-effort, отдельным запросом без пагинации.
        if let folders = try? await cloud.listSharedFoldersWithMe() {
            state.folders = folders
            await resolveUsers(Set(folders.map(\.ownerUserID)))
        }
        state.isPlaceholder = false
    }

    func loadMoreIfNeeded(current item: SharedFileEntry) async {
        guard state.canLoadMore, !state.isLoadingMore, !state.isPlaceholder,
              item.id == state.items.last?.id else { return }
        state.isLoadingMore = true
        do {
            let page = try await cloud.listSharedWithMe(
                limit: 60,
                cursorSharedAt: state.cursorSharedAt,
                cursorGrantID: state.cursorGrantID
            )
            state.items.append(contentsOf: page.items)
            state.cursorSharedAt = page.nextCursorSharedAt
            state.cursorGrantID = page.nextCursorGrantID
            state.canLoadMore = page.hasMore
            await resolveUsers(Set(page.items.map(\.ownerUserID)))
        } catch {
            state.snackbar = String(localized: "shared_load_failed")
        }
        state.isLoadingMore = false
    }

    /// Получить временный URL → скачать через `URLSession.shared.download` →
    /// переместить во временный файл с правильным именем → выставить
    /// `pendingExportFile` для системного `UIDocumentPickerViewController`.
    func download(_ entry: SharedFileEntry) async {
        guard !state.downloading.contains(entry.file.id) else { return }
        state.downloading.insert(entry.file.id)
        defer { state.downloading.remove(entry.file.id) }
        do {
            guard let url = try await cloud.getSharedFileDownloadUrl(fileID: entry.file.id) else {
                state.snackbar = String(localized: "shared_download_failed")
                return
            }
            let (tmp, _) = try await URLSession.shared.download(from: url)
            defer { try? FileManager.default.removeItem(at: tmp) }
            let destDir = FileManager.default.temporaryDirectory
                .appendingPathComponent("shared-\(UUID().uuidString)")
            try FileManager.default.createDirectory(at: destDir, withIntermediateDirectories: true)
            let dest = destDir.appendingPathComponent(entry.file.fileName.isEmpty ? "file" : entry.file.fileName)
            try? FileManager.default.removeItem(at: dest)
            try FileManager.default.moveItem(at: tmp, to: dest)
            state.pendingExportFile = dest
        } catch {
            state.snackbar = String(localized: "shared_download_failed")
        }
    }

    func exportShown() {
        if let file = state.pendingExportFile {
            TemporaryFileCleanup.removeFileAndEmptyParent(
                at: file,
                within: FileManager.default.temporaryDirectory
            )
        }
        state.pendingExportFile = nil
    }

    func snackbarShown() { state.snackbar = nil }

    /// Подтянуть карточки владельцев для всех новых `ownerUserID` (файлов и папок),
    /// которых ещё нет в `state.owners`. Дёргаем `UserRepository.getUser` —
    /// сервер вернёт `User { firstName, lastName, username, profilePicture* }`,
    /// заворачиваем в `CloudUser`. Ошибки одного user'a проглатываем — UI всё
    /// равно отрисует фоллбек «id N».
    private func resolveUsers(_ requested: Set<Int64>) async {
        let ids = requested.subtracting(state.owners.keys)
        guard !ids.isEmpty else { return }
        await withTaskGroup(of: (Int64, CloudUser?).self) { group in
            for id in ids {
                group.addTask { [users] in
                    let raw = try? await users.getUser(userID: id)
                    return (id, raw.map(CloudUser.init))
                }
            }
            for await (id, user) in group {
                if let user { state.owners[id] = user }
            }
        }
    }
}
