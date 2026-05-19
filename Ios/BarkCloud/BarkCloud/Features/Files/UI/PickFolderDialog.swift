import SwiftUI

struct PickFolderDialog: View {
    let repo: LocalFileRepository
    let rootPath: String
    let forbiddenPaths: Set<String>
    let onCancel: () -> Void
    let onConfirm: (String) -> Void

    @State private var currentPath: String
    @State private var directories: [FsEntry] = []
    @State private var isLoading = false

    init(repo: LocalFileRepository, rootPath: String, startPath: String, forbiddenPaths: Set<String>, onCancel: @escaping () -> Void, onConfirm: @escaping (String) -> Void) {
        self.repo = repo
        self.rootPath = rootPath
        self.forbiddenPaths = forbiddenPaths
        self.onCancel = onCancel
        self.onConfirm = onConfirm
        self._currentPath = State(initialValue: startPath)
    }

    var body: some View {
        NavigationStack {
            List {
                if currentPath != rootPath {
                    Button {
                        currentPath = (currentPath as NSString).deletingLastPathComponent
                        Task { await reload() }
                    } label: {
                        Label("..", systemImage: "chevron.up")
                    }
                }
                ForEach(directories, id: \.path) { entry in
                    Button {
                        currentPath = entry.path
                        Task { await reload() }
                    } label: {
                        Label(entry.name, systemImage: "folder")
                    }
                }
            }
            .navigationTitle(currentPathDisplay)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(String(localized: "files_dialog_cancel"), action: onCancel)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(String(localized: "files_pick_folder_confirm")) {
                        onConfirm(currentPath)
                    }
                    .disabled(forbiddenPaths.contains(currentPath))
                }
            }
        }
        .task { await reload() }
    }

    private var currentPathDisplay: String {
        currentPath == rootPath
            ? String(localized: "files_pick_folder_title")
            : (currentPath as NSString).lastPathComponent
    }

    private func reload() async {
        isLoading = true
        let entries = (try? await repo.list(at: currentPath, includeHidden: false)) ?? []
        directories = entries.filter(\.isDirectory)
        isLoading = false
    }
}
