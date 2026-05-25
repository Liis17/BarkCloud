import Foundation
import Observation

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

    func reload() async {
        state.isLoading = true
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

    func deleteDirectory(_ dir: CloudDirectory) async {
        do { try await cloud.deleteDirectory(dir.id); await reload() }
        catch { state.snackbar = domainErrorMessage(error) }
    }

    func deleteFile(_ entry: CloudFileEntry) async {
        do { try await cloud.deleteFileEntry(entry.id); await reload() }
        catch { state.snackbar = domainErrorMessage(error) }
    }

    func moveDirectory(_ dir: CloudDirectory, toDirectory targetID: String) async {
        do { try await cloud.moveDirectory(dir.id, newParentID: targetID); await reload() }
        catch { state.snackbar = domainErrorMessage(error) }
    }

    func moveFile(_ entry: CloudFileEntry, toDirectory targetID: String) async {
        do { try await cloud.moveFileEntry(entry.id, newDirectoryID: targetID); await reload() }
        catch { state.snackbar = domainErrorMessage(error) }
    }

    /// Загрузить файлы в текущую папку (фото/видео/документы).
    func upload(_ files: [(data: Data, fileName: String)]) async {
        guard !files.isEmpty else { return }
        state.isUploading = true
        var anyFailed = false
        for file in files {
            do {
                _ = try await cloud.uploadFile(data: file.data, fileName: file.fileName, toDirectory: state.directoryID)
            } catch {
                anyFailed = true
            }
        }
        state.isUploading = false
        if anyFailed { state.snackbar = String(localized: "upload_failed") }
        await reload()
    }

    func snackbarShown() { state.snackbar = nil }
}
