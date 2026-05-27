import SwiftUI
import PhotosUI
import UniformTypeIdentifiers

/// Браузер облачного хранилища: навигация по папкам, операции CRUD, загрузка
/// и открытие файлов. Рекурсивно переиспользует себя для вложенных папок.
struct CloudBrowserScreen: View {
    let directoryID: String
    let title: String

    @Environment(AppEnvironment.self) private var env
    @State private var vm: CloudBrowserViewModel?

    @State private var pickerItems: [PhotosPickerItem] = []
    @State private var showDocPicker = false
    @State private var showCreateFolder = false
    @State private var folderName = ""
    @State private var renameSubject: RenameSubject?
    @State private var renameText = ""
    @State private var moveSubject: MoveSubject?
    @State private var openFile: CloudFileEntry?

    var body: some View {
        Group {
            if let vm {
                content(vm)
            } else {
                ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar { toolbarContent }
        .task {
            if vm == nil {
                vm = CloudBrowserViewModel(directoryID: directoryID, title: title, cloud: env.cloudRepository)
            }
            await vm?.loadIfNeeded()
        }
        .onChange(of: pickerItems) { _, items in handlePhotos(items) }
        .fileImporter(isPresented: $showDocPicker, allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
            handleDocuments(result)
        }
        .alert(String(localized: "cloud_new_folder"), isPresented: $showCreateFolder) {
            TextField(String(localized: "cloud_folder_name"), text: $folderName)
            Button(String(localized: "action_create")) {
                Task { await vm?.createFolder(name: folderName) }
            }
            Button(String(localized: "action_cancel"), role: .cancel) {}
        }
        .alert(String(localized: "action_rename"), isPresented: Binding(
            get: { renameSubject != nil }, set: { if !$0 { renameSubject = nil } })) {
            TextField(String(localized: "cloud_new_name"), text: $renameText)
            Button(String(localized: "action_save")) { performRename() }
            Button(String(localized: "action_cancel"), role: .cancel) { renameSubject = nil }
        }
        .sheet(item: $moveSubject) { subject in
            CloudMovePicker(cloud: env.cloudRepository) { targetID in
                performMove(subject, to: targetID)
            }
        }
        .fullScreenCover(item: $openFile) { entry in
            NavigationStack {
                RemoteFilePreviewScreen(fileID: entry.fileID, fileName: entry.name, transfer: env.fileTransfer)
                    .toolbar {
                        ToolbarItem(placement: .topBarLeading) {
                            Button(String(localized: "action_close")) { openFile = nil }
                        }
                    }
            }
        }
    }

    @ViewBuilder
    private func content(_ vm: CloudBrowserViewModel) -> some View {
        if vm.state.isLoading {
            ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if vm.state.isEmpty {
            ScrollView {
                VStack(spacing: 16) {
                    Image(systemName: "folder")
                        .font(.system(size: 56))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                    Text("cloud_empty_folder")
                        .font(AppTypography.titleMedium)
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .frame(maxWidth: .infinity)
                .containerRelativeFrame(.vertical)
            }
            // Потянуть вниз — перезагрузить даже пустую папку.
            .refreshable { await vm.reload() }
        } else {
            List {
                ForEach(vm.state.subdirs) { dir in
                    NavigationLink {
                        CloudBrowserScreen(directoryID: dir.id, title: dir.name)
                    } label: {
                        folderRow(dir)
                    }
                    .swipeActions(edge: .trailing) {
                        deleteButton { Task { await vm.deleteDirectory(dir) } }
                        moveButton { moveSubject = .directory(dir) }
                        renameButton {
                            renameText = dir.name
                            renameSubject = .directory(dir)
                        }
                    }
                }
                ForEach(vm.state.files) { entry in
                    fileRow(entry)
                        .contentShape(Rectangle())
                        .onTapGesture { openFile = entry }
                        .swipeActions(edge: .trailing) {
                            deleteButton { Task { await vm.deleteFile(entry) } }
                            moveButton { moveSubject = .file(entry) }
                            renameButton {
                                renameText = entry.name
                                renameSubject = .file(entry)
                            }
                        }
                }
            }
            .listStyle(.plain)
            // Потянуть вниз — перезагрузить содержимое папки.
            .refreshable { await vm.reload() }
        }
    }

    @ViewBuilder
    private func folderRow(_ dir: CloudDirectory) -> some View {
        HStack(spacing: 14) {
            Image(systemName: "folder.fill")
                .font(.system(size: 22))
                .foregroundStyle(AppColors.accent)
                .frame(width: 36)
            Text(verbatim: dir.name)
                .font(AppTypography.bodyLarge)
            Spacer()
        }
    }

    @ViewBuilder
    private func fileRow(_ entry: CloudFileEntry) -> some View {
        HStack(spacing: 14) {
            if let preview = entry.asset.previewURL(preferredWidth: 128) {
                RemoteImage(url: preview, contentMode: .fill) {
                    Image(systemName: MimeIcon.iconSymbol(forFileName: entry.name))
                        .foregroundStyle(AppColors.onSurfaceVariant)
                }
                .frame(width: 36, height: 36)
                .clipShape(RoundedRectangle(cornerRadius: 6))
            } else {
                Image(systemName: MimeIcon.iconSymbol(forFileName: entry.name))
                    .font(.system(size: 22))
                    .foregroundStyle(AppColors.onSurfaceVariant)
                    .frame(width: 36)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: entry.name)
                    .font(AppTypography.bodyLarge)
                    .lineLimit(1)
                Text(verbatim: FormatUtils.formatSize(entry.asset.fileSize))
                    .font(AppTypography.bodySmall)
                    .foregroundStyle(AppColors.onSurfaceVariant)
            }
            Spacer()
        }
    }

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        ToolbarItem(placement: .topBarTrailing) {
            if vm?.state.isUploading == true {
                ProgressView()
            } else {
                Menu {
                    Button {
                        folderName = ""
                        showCreateFolder = true
                    } label: {
                        Label(String(localized: "cloud_new_folder"), systemImage: "folder.badge.plus")
                    }
                    PhotosPicker(selection: $pickerItems, maxSelectionCount: 10, matching: .any(of: [.images, .videos])) {
                        Label(String(localized: "cloud_upload_media"), systemImage: "photo")
                    }
                    Button {
                        showDocPicker = true
                    } label: {
                        Label(String(localized: "cloud_upload_document"), systemImage: "doc")
                    }
                } label: {
                    Image(systemName: "plus")
                }
            }
        }
    }

    private func deleteButton(_ action: @escaping () -> Void) -> some View {
        Button(role: .destructive, action: action) {
            Label(String(localized: "action_delete"), systemImage: "trash")
        }
    }

    private func moveButton(_ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(String(localized: "action_move"), systemImage: "folder")
        }
        .tint(.orange)
    }

    private func renameButton(_ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(String(localized: "action_rename"), systemImage: "pencil")
        }
        .tint(AppColors.accent)
    }

    private func performRename() {
        guard let subject = renameSubject else { return }
        let name = renameText
        Task {
            switch subject {
            case .directory(let dir): await vm?.renameDirectory(dir, newName: name)
            case .file(let entry): await vm?.renameFile(entry, newName: name)
            }
        }
        renameSubject = nil
    }

    private func performMove(_ subject: MoveSubject, to targetID: String) {
        Task {
            switch subject {
            case .directory(let dir): await vm?.moveDirectory(dir, toDirectory: targetID)
            case .file(let entry): await vm?.moveFile(entry, toDirectory: targetID)
            }
        }
        moveSubject = nil
    }

    private func handlePhotos(_ items: [PhotosPickerItem]) {
        guard !items.isEmpty else { return }
        Task {
            var files: [(data: Data, fileName: String)] = []
            for item in items {
                if let data = try? await item.loadTransferable(type: Data.self) {
                    let ext = item.supportedContentTypes.first?.preferredFilenameExtension ?? "dat"
                    files.append((data, "\(UUID().uuidString).\(ext)"))
                }
            }
            pickerItems = []
            await vm?.upload(files)
        }
    }

    private func handleDocuments(_ result: Result<[URL], Error>) {
        guard case .success(let urls) = result, !urls.isEmpty else { return }
        Task {
            var files: [(data: Data, fileName: String)] = []
            for url in urls {
                let scoped = url.startAccessingSecurityScopedResource()
                defer { if scoped { url.stopAccessingSecurityScopedResource() } }
                if let data = try? Data(contentsOf: url) {
                    files.append((data, url.lastPathComponent))
                }
            }
            await vm?.upload(files)
        }
    }

    private enum RenameSubject: Identifiable {
        case directory(CloudDirectory)
        case file(CloudFileEntry)
        var id: String {
            switch self {
            case .directory(let d): return "d-\(d.id)"
            case .file(let f): return "f-\(f.id)"
            }
        }
    }

    private enum MoveSubject: Identifiable {
        case directory(CloudDirectory)
        case file(CloudFileEntry)
        var id: String {
            switch self {
            case .directory(let d): return "d-\(d.id)"
            case .file(let f): return "f-\(f.id)"
            }
        }
    }
}

/// Лёгкий выбор папки-назначения для перемещения: навигация по облаку с кнопкой
/// «Переместить сюда».
private struct CloudMovePicker: View {
    let cloud: CloudRepository
    let onPick: (String) -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var currentID = ""
    @State private var currentName = ""
    @State private var subdirs: [CloudDirectory] = []
    @State private var isLoading = true

    var body: some View {
        NavigationStack {
            List {
                ForEach(subdirs) { dir in
                    Button {
                        currentID = dir.id
                        currentName = dir.name
                    } label: {
                        HStack {
                            Image(systemName: "folder.fill").foregroundStyle(AppColors.accent)
                            Text(verbatim: dir.name)
                            Spacer()
                            Image(systemName: "chevron.right").foregroundStyle(AppColors.onSurfaceVariant)
                        }
                    }
                    .buttonStyle(.plain)
                }
                if subdirs.isEmpty && !isLoading {
                    Text("cloud_move_no_subfolders").foregroundStyle(AppColors.onSurfaceVariant)
                }
            }
            .overlay { if isLoading { ProgressView() } }
            .navigationTitle(currentID.isEmpty ? String(localized: "cloud_root") : currentName)
            .navigationBarTitleDisplayMode(.inline)
            .task(id: currentID) { await load() }
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button(String(localized: "action_cancel")) { dismiss() }
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Button(String(localized: "cloud_move_here")) {
                        onPick(currentID)
                        dismiss()
                    }
                }
            }
        }
    }

    private func load() async {
        isLoading = true
        if let listing = try? await cloud.listDirectory(currentID) {
            subdirs = listing.subdirs
        }
        isLoading = false
    }
}
