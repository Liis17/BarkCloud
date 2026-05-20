import Foundation
import Observation

@MainActor
@Observable
final class LocalBrowserViewModel {
    var state: BrowserUiState
    var pendingEvent: BrowserEvent?

    private let repo: LocalFileRepository
    private let rootLabel: String

    init(repo: LocalFileRepository, initialPath: String, rootLabel: String) {
        let root = StoragePermission.externalRoot.path
        let path = initialPath.isEmpty ? root : initialPath
        let title = path == root ? rootLabel : (path as NSString).lastPathComponent
        self.repo = repo
        self.rootLabel = rootLabel
        self.state = BrowserUiState(currentPath: path, rootPath: root, title: title)
        Task { await refresh() }
    }

    func refresh() async {
        state.isLoading = true
        do {
            let entries = try await repo.list(at: state.currentPath, includeHidden: state.showHidden)
            state.entries = applySort(entries, by: state.sort)
        } catch {
            pendingEvent = .toast(error.localizedDescription)
        }
        state.isLoading = false
    }

    func enter(_ entry: FsEntry) {
        switch entry {
        case .directory(let d):
            state.currentPath = d.path
            state.title = d.name
            state.selection.removeAll()
            Task { await refresh() }
        case .file(let f):
            pendingEvent = .openFile(URL(fileURLWithPath: f.path))
        }
    }

    @discardableResult
    func goUp() -> Bool {
        guard state.canGoUp else { return false }
        let parent = (state.currentPath as NSString).deletingLastPathComponent
        state.currentPath = parent
        state.title = parent == state.rootPath ? rootLabel : (parent as NSString).lastPathComponent
        state.selection.removeAll()
        Task { await refresh() }
        return true
    }

    func toggleSelect(_ entry: FsEntry) {
        if state.selection.contains(entry.path) {
            state.selection.remove(entry.path)
        } else {
            state.selection.insert(entry.path)
        }
    }

    func selectAll() {
        state.selection = Set(state.entries.map(\.path))
    }

    func clearSelection() {
        state.selection.removeAll()
    }

    func setSort(_ sort: FsSort) {
        state.sort = sort
        state.entries = applySort(state.entries, by: sort)
    }

    func toggleHidden() {
        state.showHidden.toggle()
        Task { await refresh() }
    }

    func createFolder(name: String) {
        Task {
            do {
                try await repo.createDir(parentPath: state.currentPath, name: name)
                await refresh()
            } catch let err as LocalFileRepository.OpError {
                pendingEvent = .toast(String(localized: String.LocalizationValue(err.messageKey)))
            } catch {
                pendingEvent = .toast(error.localizedDescription)
            }
        }
    }

    func rename(_ entry: FsEntry, newName: String) {
        Task {
            do {
                try await repo.rename(entry: entry, newName: newName)
                state.selection.removeAll()
                await refresh()
            } catch let err as LocalFileRepository.OpError {
                pendingEvent = .toast(String(localized: String.LocalizationValue(err.messageKey)))
            } catch {
                pendingEvent = .toast(error.localizedDescription)
            }
        }
    }

    func deleteSelected() {
        let entries = selectedEntries()
        guard !entries.isEmpty else { return }
        state.pendingOp = PendingOp(kind: .delete, progress: 0)
        Task {
            do {
                try await repo.delete(entries: entries)
                state.selection.removeAll()
                state.pendingOp = nil
                await refresh()
            } catch {
                state.pendingOp = nil
                pendingEvent = .toast(error.localizedDescription)
            }
        }
    }

    func copySelected(to dir: String) {
        runProgressOp(.copy) { [weak self] in
            guard let self else { return }
            try await self.repo.copy(entries: self.selectedEntries(), to: dir) { [weak self] p in
                Task { @MainActor in self?.state.pendingOp?.progress = p }
            }
        }
    }

    func moveSelected(to dir: String) {
        runProgressOp(.move) { [weak self] in
            guard let self else { return }
            try await self.repo.move(entries: self.selectedEntries(), to: dir) { [weak self] p in
                Task { @MainActor in self?.state.pendingOp?.progress = p }
            }
        }
    }

    func shareSelected() {
        let urls = selectedEntries().map { URL(fileURLWithPath: $0.path) }
        pendingEvent = .shareFiles(urls)
    }

    func shareSingle(_ entry: FsEntry) {
        pendingEvent = .shareFiles([URL(fileURLWithPath: entry.path)])
    }

    func selectedEntries() -> [FsEntry] {
        state.entries.filter { state.selection.contains($0.path) }
    }

    func eventConsumed() {
        pendingEvent = nil
    }

    private func runProgressOp(_ kind: PendingOp.Kind, _ operation: @Sendable @escaping () async throws -> Void) {
        state.pendingOp = PendingOp(kind: kind, progress: 0)
        Task { [weak self] in
            do {
                try await operation()
                await MainActor.run {
                    self?.state.selection.removeAll()
                    self?.state.pendingOp = nil
                }
                await self?.refresh()
            } catch {
                await MainActor.run {
                    self?.state.pendingOp = nil
                    self?.pendingEvent = .toast(error.localizedDescription)
                }
            }
        }
    }
}
