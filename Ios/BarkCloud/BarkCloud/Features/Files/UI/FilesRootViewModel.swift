import Foundation
import Observation

/// Папка облачного хранилища (зеркалит `DirectoryInfo` из `files_api.proto`).
struct ServerFolder: Identifiable, Hashable {
    let id: String
    let name: String
}

struct FilesRootUiState {
    /// Пока `true` — список папок сервера рисуется скелетон-строками (.redacted).
    var serverFoldersLoading: Bool = true
    var serverFolders: [ServerFolder] = []
}

@MainActor
@Observable
final class FilesRootViewModel {
    var state = FilesRootUiState()

    /// TODO: подключить `CloudApi.ListDirectory(root)` и сложить subdirs в
    /// `state.serverFolders`, затем `serverFoldersLoading = false`.
    /// Пока получение с сервера не реализовано — остаёмся в скелетон-режиме.
    func loadServerFolders() async {
        // no-op (server retrieval not implemented yet)
    }
}
