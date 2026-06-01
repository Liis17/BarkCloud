import Foundation
import Observation
import UIKit

struct CloudBrowserUiState {
    var directoryID: String       // "" = корень
    var title: String
    var crumbs: [PathCrumb] = []
    var subdirs: [CloudDirectory] = []
    var files: [CloudFileEntry] = []
    var isLoading = true
    var isUploading = false
    var snackbar: String?

    var isEmpty: Bool { subdirs.isEmpty && files.isEmpty }
}

@MainActor
@Observable
final class CloudBrowserViewModel {
    var state: CloudBrowserUiState

    /// Очередь отложенного удаления — внизу экрана показывается snackbar с
    /// отсчётом, до выполнения запрос на сервер не уходит.
    let pendingDelete = PendingDelete()

    private let cloud: CloudRepository
    private var didLoad = false

    init(directoryID: String, title: String, cloud: CloudRepository) {
        self.cloud = cloud
        self.state = CloudBrowserUiState(directoryID: directoryID, title: title)
    }

    var isRoot: Bool { state.directoryID.isEmpty }

    func loadIfNeeded() async {
        guard !didLoad else { return }
        didLoad = true
        await reload()
    }

    /// Перезагрузка содержимого папки. `showSpinner` поднимает полноэкранный
    /// `ProgressView` — нужно при программных обновлениях (создание/переименование/
    /// удаление/перемещение). При pull-to-refresh передаём `false`: иначе экран
    /// свернул бы `List` (носитель `.refreshable`) в `ProgressView`, SwiftUI отменил
    /// бы задачу обновления и gRPC-запрос упал бы с «the transport threw an
    /// unexpected error». Спиннер потягивания рисует сам `.refreshable`.
    func reload(showSpinner: Bool = true) async {
        // Если в очереди есть отложенное удаление — выполняем его до перезагрузки,
        // иначе сервер вернёт нам обратно файл, который пользователь только что
        // визуально убрал.
        await pendingDelete.flushIfAny()
        if showSpinner { state.isLoading = true }
        do {
            let listing = try await cloud.listDirectory(state.directoryID)
            state.subdirs = listing.subdirs
            state.files = listing.files
            if !isRoot {
                state.crumbs = (try? await cloud.path(directoryID: state.directoryID)) ?? []
            }
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
        state.isLoading = false
    }

    func createFolder(name: String) async {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return }
        do {
            _ = try await cloud.createDirectory(parentID: state.directoryID, name: trimmed)
            await reload()
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }

    func renameDirectory(_ dir: CloudDirectory, newName: String) async {
        do { try await cloud.renameDirectory(dir.id, newName: newName); await reload() }
        catch { state.snackbar = domainErrorMessage(error) }
    }

    func renameFile(_ entry: CloudFileEntry, newName: String) async {
        do { try await cloud.renameFileEntry(entry.id, newName: newName); await reload() }
        catch { state.snackbar = domainErrorMessage(error) }
    }

    /// Оптимистичное удаление папки: сразу убираем из сетки и кладём в очередь
    /// отложенного удаления — реальный gRPC-запрос уйдёт, когда snackbar отсчитает
    /// свои 5 секунд (или пользователь поставит другое удаление в очередь).
    func deleteDirectory(_ dir: CloudDirectory) {
        guard let index = state.subdirs.firstIndex(where: { $0.id == dir.id }) else { return }
        state.subdirs.remove(at: index)
        pendingDelete.schedule(
            label: dir.name,
            action: { [weak self, cloud] in
                do { try await cloud.deleteDirectory(dir.id) }
                catch {
                    // Сервер не дал удалить — возвращаем папку на место и сообщаем.
                    self?.state.snackbar = domainErrorMessage(error)
                    await self?.reload(showSpinner: false)
                }
            },
            onUndo: { [weak self] in
                guard let self else { return }
                let position = min(index, state.subdirs.count)
                state.subdirs.insert(dir, at: position)
            }
        )
    }

    /// Оптимистичное удаление файла (см. `deleteDirectory`).
    func deleteFile(_ entry: CloudFileEntry) {
        guard let index = state.files.firstIndex(where: { $0.id == entry.id }) else { return }
        state.files.remove(at: index)
        pendingDelete.schedule(
            label: entry.name,
            action: { [weak self, cloud] in
                do { try await cloud.deleteFileEntry(entry.id) }
                catch {
                    self?.state.snackbar = domainErrorMessage(error)
                    await self?.reload(showSpinner: false)
                }
            },
            onUndo: { [weak self] in
                guard let self else { return }
                let position = min(index, state.files.count)
                state.files.insert(entry, at: position)
            }
        )
    }

    func moveDirectory(_ dir: CloudDirectory, toDirectory targetID: String) async {
        do { try await cloud.moveDirectory(dir.id, newParentID: targetID); await reload() }
        catch { state.snackbar = domainErrorMessage(error) }
    }

    func moveFile(_ entry: CloudFileEntry, toDirectory targetID: String) async {
        do { try await cloud.moveFileEntry(entry.id, newDirectoryID: targetID); await reload() }
        catch { state.snackbar = domainErrorMessage(error) }
    }

    /// Поставить файлы в фоновую очередь (`BackgroundUploadCoordinator`). UI не
    /// ждёт завершения — загрузка переживёт сворачивание/kill приложения. По
    /// событию `onJobCompleted` в `AppEnvironment` файл будет привязан к папке.
    func upload(_ files: [(data: Data, fileName: String)]) async {
        guard !files.isEmpty else { return }
        var anyFailed = false
        for file in files {
            do {
                _ = try await cloud.enqueueBackgroundUpload(
                    data: file.data,
                    fileName: file.fileName,
                    toDirectory: state.directoryID,
                    source: .manual
                )
            } catch {
                anyFailed = true
            }
        }
        if anyFailed { state.snackbar = String(localized: "upload_failed") }
    }

    func snackbarShown() { state.snackbar = nil }

    /// Создать публичную ссылку на файл и положить URL в буфер обмена.
    /// Зеркалит `GalleryViewModel.makePublic` / `MediaGridViewModel.makePublic`.
    func makePublic(_ entry: CloudFileEntry) async {
        do {
            let link = try await cloud.createShare(fileID: entry.fileID, name: entry.name)
            guard let url = link.url else {
                state.snackbar = String(localized: "shared_load_failed")
                return
            }
            UIPasteboard.general.url = url
            state.snackbar = String(localized: "snack_public_copied")
        } catch {
            state.snackbar = domainErrorMessage(error)
        }
    }
}
